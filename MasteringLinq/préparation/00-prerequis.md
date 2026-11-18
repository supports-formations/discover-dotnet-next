# 00 — Pré-requis : comprendre où se joue la performance

> Workshop EF Core avancé (.NET 10/11) — base `StarWarsTrade` (38 M de lignes, SQL Server 2022 en Docker).
> **Aucun code n'est fourni en dehors de ce document** : tous les extraits sont à copier dans votre projet `MasteringLinq` pour les tester vous-même.
> Les effets annoncés sont **des effets attendus à vérifier** avec la boîte à outils ci-dessous ; aucun chiffre n'est donné volontairement.

## Sommaire

- [Boîte à outils : mesurer et voir ce qui se passe](#boîte-à-outils--mesurer-et-voir-ce-qui-se-passe)
- [0.1 `IQueryable` vs `IEnumerable` (arbre d'expression vs délégué)](#01-iqueryable-vs-ienumerable)
- [0.2 Exécution différée vs immédiate](#02-exécution-différée-vs-immédiate)
- [0.3 Streaming vs buffering](#03-streaming-vs-buffering)
- [0.4 Le pipeline EF Core (compilation, cache, pool) — et la comparaison avec une procédure stockée](#04-le-pipeline-ef-core)
- [0.5 Évaluation client](#05-évaluation-client)
- [0.6 Composition d'`IQueryable`](#06-composition-diqueryable)
- [0.7 Sémantique SQL vs C#](#07-sémantique-sql-vs-c)
- [0.8 Les trois axes de mesure — LINQ vs procédure stockée, avec et sans index](#08-les-trois-axes-de-mesure)
- [Script de nettoyage](#script-de-nettoyage)

---

## Boîte à outils : mesurer et voir ce qui se passe

Tout le workshop repose sur trois questions : **quel SQL est envoyé ? combien ça coûte côté SQL Server ? combien ça coûte côté .NET ?** Voici de quoi répondre à chacune.

### A. Côté .NET : temps, allocations, requêtes, lignes lues

Squelette de `Program.cs` à coller dans votre projet (il suppose le `StarWarsContext` et les modèles déjà générés dans `MasteringLinq/Data` et `MasteringLinq/Models`).

```csharp
using System.Data.Common;
using System.Diagnostics;
using MasteringLinq.Data;
using MasteringLinq.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

const string Cs = "Server=localhost,1433;Database=StarWarsTrade;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False;Connect Timeout=15";

var compteur = new CompteurSql();

// Fabrique un contexte ; afficherSql = true imprime chaque commande SQL envoyée à la base.
StarWarsContext NewDb(bool afficherSql = false)
{
    var b = new DbContextOptionsBuilder<StarWarsContext>()
        .UseSqlServer(Cs, o => o.CommandTimeout(300))
        .AddInterceptors(compteur);
    if (afficherSql)
        b.LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information)
         .EnableSensitiveDataLogging();            // montre les VALEURS des paramètres (jamais en production)
    return new StarWarsContext(b.Options);
}

// Exécute `action` avec un contexte neuf et affiche : temps, allocations, GC, nb de requêtes SQL, lignes lues.
async Task<T> Mesurer<T>(string nom, Func<StarWarsContext, Task<T>> action, bool afficherSql = false)
{
    await using var db = NewDb(afficherSql);
    compteur.Reset();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long a0 = GC.GetTotalAllocatedBytes(true);
    int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);

    var sw = Stopwatch.StartNew();
    var resultat = await action(db);
    sw.Stop();

    Console.WriteLine($"{nom,-52} {sw.ElapsedMilliseconds,7:N0} ms | {(GC.GetTotalAllocatedBytes(true) - a0) / 1_048_576.0,8:N1} Mo alloués" +
                      $" | GC0:{GC.CollectionCount(0) - g0} GC2:{GC.CollectionCount(2) - g2}" +
                      $" | SQL: {compteur.Requetes} req, {compteur.Lignes:N0} lignes | résultat: {resultat}");
    return resultat;
}

// ───── vos cas de test ici ─────
// await Mesurer("exemple", async db => await db.Goods.CountAsync());

// Les déclarations de types viennent APRÈS les instructions de haut niveau.
public sealed class CompteurSql : DbCommandInterceptor
{
    public int Requetes; public long Lignes;
    public void Reset() { Requetes = 0; Lignes = 0; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r)
    { Requetes++; return r; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
    { Requetes++; return new(r); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r)
    { Requetes++; return r; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
    { Requetes++; return new(r); }
    public override InterceptionResult DataReaderDisposing(DbCommand c, DataReaderDisposingEventData e, InterceptionResult r)
    { Lignes += e.ReadCount; return r; }
}
```

**Règles de mesure** (à respecter pour que les comparaisons soient honnêtes) :
1. **Compilez en `Release`** et lancez sans débogueur : `dotnet run -c Release`.
2. **Exécutez chaque cas deux fois** et ignorez la première (compilation EF, JIT, plan SQL, pages SQL Server en mémoire).
3. **Ne comparez que des états identiques** (cache chaud contre cache chaud).
4. **Un seul changement à la fois**, puis on remesure.
5. Vérifiez que les deux variantes retournent **le même résultat** (le champ « résultat » est là pour ça).

### B. Voir le SQL généré par EF Core

| Outil | Comment | Ce qu'on voit |
|---|---|---|
| `ToQueryString()` | `Console.WriteLine(requete.ToQueryString());` | Le SQL **sans l'exécuter**, avec les déclarations de paramètres |
| Journal des commandes | `LogTo(...)` (voir `NewDb(true)`) | Chaque commande réellement envoyée, durée en ms, paramètres |
| `TagWith("nom")` | `db.Goods.TagWith("demo-0.1").Where(...)` | Un commentaire `-- demo-0.1` en tête du SQL : on retrouve la requête dans les DMV et le Query Store |
| `TagWithCallSite()` | idem | Fichier et ligne C# ayant produit la requête |

```csharp
var q = db.Goods.TagWith("demo-ToQueryString").Where(g => g.IsContraband && g.BasePrice > 99_000);
Console.WriteLine(q.ToQueryString());   // rien n'est envoyé à la base
```

### C. Côté SQL Server : coût d'une requête

**Se connecter** depuis le terminal (ou utilisez SSMS / Azure Data Studio avec la même chaîne de connexion) :

```bash
docker exec -it masteringlinq-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'StarWars_Pwd123!' -d StarWarsTrade
```
(dans `sqlcmd`, terminez chaque lot par `GO` sur sa propre ligne.)

**1. Compteurs d'I/O et de temps** — l'outil de comparaison n° 1 :

```sql
SET STATISTICS IO, TIME ON;
-- votre requête ici
```

| Information | Où | Comment la lire |
|---|---|---|
| `logical reads` | Messages | Pages de 8 Ko lues en mémoire. **Indicateur le plus fiable** pour comparer deux versions d'une requête : il ne dépend ni de la charge de la machine ni du cache |
| `physical reads` / `read-ahead reads` | Messages | Pages lues sur disque : non nul quand le cache est froid |
| `Scan count` | Messages | Nombre de parcours de la table/de l'index |
| `CPU time` / `elapsed time` | Messages | Temps CPU et durée de la requête côté serveur |

**2. Plan d'exécution : ce que fait l'optimiseur de requêtes SQL Server de la requête reçue**
- *Azure Data Studio / SSMS* : activer « **Include Actual Execution Plan** » (Ctrl+M dans SSMS) avant d'exécuter ; « Estimated » (Ctrl+L) donne le plan sans exécuter.
- En texte : `SET SHOWPLAN_TEXT ON;` (plan estimé sans exécution, à terminer par `SET SHOWPLAN_TEXT OFF;`) ou `SET STATISTICS XML ON;` (plan réel en XML, ouvrable dans les outils graphiques).

Que regarder dans un plan :

| Élément | Bon signe | Mauvais signe |
|---|---|---|
| Accès aux données | `Index Seek` | `Clustered Index Scan` / `Table Scan` sur une grosse table |
| Colonnes manquantes | index *couvrant* (aucun lookup) | `Key Lookup` répété pour chaque ligne |
| Estimations | lignes estimées ≈ lignes réelles | écart de plusieurs ordres de grandeur (statistiques, *parameter sniffing*) |
| Tri / regroupement | tri absorbé par un index | `Sort` volumineux, avertissement de *spill* vers `tempdb` |
| Parallélisme | choisi quand la requête est lourde | — (informatif) |
| Prédicat | « Seek Predicates » | prédicat appliqué en « Predicate » après un scan |

**3. Les requêtes déjà exécutées (plan en cache, statistiques cumulées)** :

```sql
SELECT TOP 20
       qs.execution_count,
       qs.total_logical_reads / qs.execution_count AS reads_moyens,
       qs.total_worker_time  / qs.execution_count / 1000.0 AS cpu_ms_moyen,
       qs.total_elapsed_time / qs.execution_count / 1000.0 AS duree_ms_moyenne,
       st.text AS requete,
       qp.query_plan
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle)   AS st
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
WHERE st.text LIKE '%demo-ToQueryString%'          -- le tag posé avec TagWith
ORDER BY qs.last_execution_time DESC;
```

**4. Contrôler le contexte de la mesure**

```sql
-- Cache FROID : vide le cache de pages (environnement de test uniquement !)
CHECKPOINT; DBCC DROPCLEANBUFFERS;

-- Plan cache : repartir de zéro (affecte TOUT le serveur ; OK sur ce conteneur de test)
DBCC FREEPROCCACHE;

-- Taille du plan cache pour une famille de requêtes (voir 0.4)
SELECT COUNT(*) AS plans
FROM sys.dm_exec_cached_plans cp
CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
WHERE st.text LIKE '%FROM [[]Goods]%' AND st.text NOT LIKE '%dm_exec_cached_plans%';

-- Les requêtes en cours d'exécution
SELECT r.session_id, r.status, r.wait_type, r.logical_reads, r.cpu_time, r.total_elapsed_time, t.text
FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.session_id <> @@SPID AND r.session_id > 50;
```

### D. Côté processus .NET : mémoire et GC en direct

```bash
dotnet tool install --global dotnet-counters
dotnet-counters monitor --process-id <PID> System.Runtime
```
Surveillez `GC Heap Size`, `Gen 0/1/2 GC Count`, `Allocation Rate`, `Working Set`. Le PID : `dotnet-counters ps`.

Pour un **pic de mémoire** depuis le code (utile en 0.3) :

```csharp
long pic = 0; using var arret = new CancellationTokenSource();
var echantillonneur = Task.Run(async () => { while (!arret.IsCancellationRequested) { pic = Math.Max(pic, GC.GetTotalMemory(false)); await Task.Delay(15); } });
// … code à mesurer …
arret.Cancel(); await echantillonneur;
Console.WriteLine($"pic du tas managé : {pic / 1_048_576.0:N0} Mo");
```

### E. Index de démonstration

Les index créés pour les démonstrations portent le préfixe `IX_Lab_` ; les procédures, le préfixe `Lab_`. Le **script de nettoyage** en fin de document supprime tout. Mesurez toujours **sans index**, créez l'index, **remesurez**, puis supprimez-le.

---

## 0.1 `IQueryable` vs `IEnumerable`

**Axe concerné : 🗄️ SQL + ⚙️ .NET (CPU) + 🧠 mémoire.**

### La notion

`IQueryable<T>` transporte un **arbre d'expression** : EF Core le lit et le traduit en SQL. `IEnumerable<T>` transporte un **délégué compilé** (`Func`) : il ne peut s'exécuter que dans le CLR, ligne par ligne. Le compilateur choisit l'un ou l'autre **selon le type de la variable** à laquelle on applique `Where` :

| Écriture | Méthode appelée | Où le filtre s'exécute |
|---|---|---|
| `db.Goods.Where(g => ...)` | `Queryable.Where(Expression<Func<..>>)` | SQL Server |
| `db.Goods.Where(unFunc)` | `Enumerable.Where(Func<..>)` | .NET, après avoir **tout rapatrié** |
| `IEnumerable<Good> s = db.Goods; s.Where(...)` | `Enumerable.Where` | .NET (le `IQueryable` est perdu par le *upcast*) |

### Dans quels cas on est concerné

- Méthode de repository dont le paramètre est un `Func<T,bool>`, ou qui retourne `IEnumerable<T>` / `List<T>` alors que l'appelant va encore filtrer.
- Variable ou champ déclaré `IEnumerable<T>` alors que la source est un `DbSet`.
- `AsEnumerable()` / `ToList()` placé **avant** un `Where`, un `Count`, un `OrderBy`.
- Aucun avertissement du compilateur : le bug n'apparaît qu'avec le volume.

### Exemple sans l'amélioration

```csharp
// ❌ Le lambda est compilé en délégué : Enumerable.Where, la table ENTIÈRE est lue puis filtrée en C#
await Mesurer("AVANT : Func (filtre en mémoire)", db =>
{
    Func<Good, bool> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return Task.FromResult(db.Goods.AsNoTracking().Where(contrebande).ToList().Count);
}, afficherSql: true);
```

**SQL envoyé** (visible dans le journal) : `SELECT ... FROM [Goods] AS [g]` — **aucun `WHERE`**.
**Pourquoi c'est mauvais** : 1 M de lignes sont lues par SQL Server, transférées sur le réseau, matérialisées en objets .NET (allocations, GC), puis ~800 seulement sont conservées. Les trois axes sont pénalisés.

### Exemple avec l'amélioration

```csharp
// ✔ Expression : traduite en WHERE
await Mesurer("APRÈS : Expression (filtre en SQL)", async db =>
{
    System.Linq.Expressions.Expression<Func<Good, bool>> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return (await db.Goods.AsNoTracking().Where(contrebande).ToListAsync()).Count;
}, afficherSql: true);
```

**Ce qu'il faut observer** : le SQL contient `WHERE [g].[IsContraband] = CAST(1 AS bit) AND [g].[BasePrice] > 99000.0` ; « lignes lues » tombe de ~1 M à quelques centaines ; les allocations s'effondrent.

Deux autres variantes du même piège, à essayer :

```csharp
// ❌ Upcast silencieux sur Inhabitants (4 M)
await Mesurer("AVANT : IEnumerable.Count", db =>
{
    IEnumerable<Inhabitant> source = db.Inhabitants.AsNoTracking();
    return Task.FromResult(source.Count(i => i.IsActive && i.Credits > 200_000 && i.MidiChlorians > 15_000));
});
// ✔
await Mesurer("APRÈS : IQueryable.CountAsync", db =>
    db.Inhabitants.CountAsync(i => i.IsActive && i.Credits > 200_000 && i.MidiChlorians > 15_000));

// ❌ Repository générique avec Func sur Trades (6 M)
List<Trade> Get(StarWarsContext db, Func<Trade, bool> p) =>
    db.Trades.AsNoTracking().Where(p).OrderByDescending(t => t.TradedAt).Take(20).ToList();
await Mesurer("AVANT : Get(Func)", db => Task.FromResult(Get(db, t => t.GoodId == 424_242).Count));
// ✔ même méthode avec Expression<Func<Trade,bool>> : l'index IX_Trades_GoodId devient utilisable
```

### Sans index / avec index

Dans le cas `Expression` sur `Goods`, **aucun index ne sert** : SQL Server lit toute la table. Dans SQL Server Management Studio / `sqlcmd` :

```sql
SET STATISTICS IO, TIME ON;
SELECT g.Id, g.Sku, g.Name, g.Category, g.BasePrice, g.WeightKg, g.IsContraband, g.IsPerishable
FROM dbo.Goods AS g
WHERE g.IsContraband = 1 AND g.BasePrice > 99000;     -- notez logical reads + plan (Clustered Index Scan)
GO
-- Index filtré : ne contient que les ~8 % de biens de contrebande, trié par prix
CREATE INDEX IX_Lab_Goods_Contraband ON dbo.Goods (BasePrice)
    INCLUDE (Sku, Name, Category, WeightKg, IsContraband, IsPerishable)
    WHERE IsContraband = 1;
GO
SELECT g.Id, g.Sku, g.Name, g.Category, g.BasePrice, g.WeightKg, g.IsContraband, g.IsPerishable
FROM dbo.Goods AS g
WHERE g.IsContraband = 1 AND g.BasePrice > 99000;     -- comparez : Index Seek, logical reads
GO
DROP INDEX IX_Lab_Goods_Contraband ON dbo.Goods;
```
Puis relancez **l'exemple C#** `Expression` avant/après la création de l'index et comparez « durée SQL ».
*À retenir* : l'`Expression` ouvre la **possibilité** d'utiliser un index ; le `Func` l'interdit. L'index, lui, est la réponse à la question « SQL lit-il trop de pages ? ».

Cas du repository : `Trades.GoodId` **a déjà** un index (`IX_Trades_GoodId`, créé par convention sur les clés étrangères) ; comparez le plan de `WHERE GoodId = 424242` (Index Seek + Key Lookup) avec la version `Func` (aucun filtre côté SQL).

### 🧪 TP 0.1 — « La fuite d'`IQueryable` »

*Énoncé.* Voici un repository :

```csharp
public class GoodRepository(StarWarsContext db)
{
    public IEnumerable<Good> GetAll() => db.Goods.AsNoTracking();
    public List<Good> Where(Func<Good, bool> p) => db.Goods.AsNoTracking().Where(p).ToList();
}
```
1. Utilisez-le pour obtenir les biens périssables de catégorie `3` dont le prix est inférieur à 100 et affichez-en le nombre, avec `Mesurer` et `afficherSql: true`. Notez le SQL, les lignes lues, les allocations.
2. Corrigez le repository pour que **seuls les biens correspondants** quittent SQL Server, sans changer le code appelant (`repo.Where(g => ...)`).
3. Dites en une phrase pourquoi le compilateur n'a signalé aucun problème.

<details><summary>Correction</summary>

```csharp
// 1) Usage « naïf » : le SQL n'a aucun WHERE, 1 M de lignes sont lues
await Mesurer("TP avant", db => Task.FromResult(
    new GoodRepository(db).Where(g => g.IsPerishable && g.Category == 3 && g.BasePrice < 100).Count));

// 2) Repository corrigé
public class GoodRepository(StarWarsContext db)
{
    public IQueryable<Good> GetAll() => db.Goods.AsNoTracking();                       // reste composable
    public List<Good> Where(System.Linq.Expressions.Expression<Func<Good, bool>> p)    // Expression, pas Func
        => db.Goods.AsNoTracking().Where(p).ToList();
}
// L'appelant écrit exactement la même chose : repo.Where(g => g.IsPerishable && ...)
// → le même lambda est maintenant compilé en arbre d'expression et envoyé en WHERE.
```
3) `Func<T,bool>` et `Expression<Func<T,bool>>` sont deux types valides ; `Enumerable.Where` accepte le premier, `Queryable.Where` le second : les deux compilent, seule la sémantique d'exécution change.
</details>

---

## 0.2 Exécution différée vs immédiate

**Axe concerné : 🗄️ SQL (nombre d'allers-retours et de scans) + ⚙️ .NET.**

### La notion

Une requête LINQ est une **recette**. Le SQL part quand on **énumère** : `ToList`, `ToArray`, `First*`, `Single*`, `Count`, `Sum`, `Min`, `Max`, `Any`, `All`, `Contains`, `foreach`, `await foreach`. Chaque énumération **rejoue la requête** depuis le début.

### Dans quels cas on est concerné

- La même variable `IQueryable` utilisée plusieurs fois (`if (q.Any()) { ... q.Count() ... foreach (var x in q) }`).
- `for (i < q.Count())`, `q.ElementAt(i)` dans une boucle.
- Variable capturée modifiée après la construction de la requête (la valeur est lue **à l'exécution**).
- `IQueryable` retourné par une méthode dont le `DbContext` est déjà libéré (exception tardive).

### Exemple sans l'amélioration

```csharp
// ❌ 4 énumérations de la même recette = 4 requêtes SQL, donc 4 parcours de la table Planets (3 M)
await Mesurer("AVANT : Any + Count + Max + ToList", async db =>
{
    var q = db.Planets.AsNoTracking().Where(p => p.HasSpaceport && p.Population > 99_000_000_000);
    var any = await q.AnyAsync();
    var n   = await q.CountAsync();
    var max = await q.MaxAsync(p => p.Population);
    var l   = await q.ToListAsync();
    return $"any={any} n={n} max={max} liste={l.Count}";
}, afficherSql: true);

// ❌ Le classique : Count() et Skip(i).First() réexécutés à CHAQUE tour de boucle
await Mesurer("AVANT : boucle for", async db =>
{
    var q = db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30);
    decimal somme = 0;
    for (var i = 0; i < await q.CountAsync(); i++)
        somme += (await q.Skip(i).FirstAsync()).BasePrice;
    return somme;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ On exécute une fois, on exploite le résultat en mémoire
await Mesurer("APRÈS : une exécution", async db =>
{
    var l = await db.Planets.AsNoTracking().Where(p => p.HasSpaceport && p.Population > 99_000_000_000).ToListAsync();
    return $"any={l.Count > 0} n={l.Count} max={l.Max(p => p.Population)} liste={l.Count}";
});

await Mesurer("APRÈS : boucle sur la liste", async db =>
{
    var l = await db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30).ToListAsync();
    decimal somme = 0;
    for (var i = 0; i < l.Count; i++) somme += l[i].BasePrice;
    return somme;
});
```

**Ce qu'il faut observer** : la colonne « SQL : N req » (4 → 1 ; 61 → 1) ; dans `sqlcmd`, `SET STATISTICS IO ON` puis la requête du `WHERE HasSpaceport = 1 AND Population > ...` : les *logical reads* sont **multipliés par le nombre d'exécutions**.

> ⚠ **Matérialiser n'est pas toujours la bonne réponse.** Si l'objectif est un agrégat (`Count` par catégorie), laissez SQL le calculer : un `GroupBy` côté serveur est plus léger que de ramener toutes les lignes pour les compter en mémoire (voir 0.8).

**Capture de variable** (à tester) :

```csharp
await using var db = NewDb();
var seuil = 100m;
var q = db.Goods.Where(g => g.BasePrice < seuil);
seuil = 5m;                                   // modifié APRÈS avoir écrit la requête
Console.WriteLine(q.ToQueryString());         // le paramètre @__seuil_0 vaut 5, pas 100
```

### Sans index / avec index

Sans objet ici : l'enjeu est le **nombre de requêtes**. (Un index réduit le coût de *chaque* exécution, il ne supprime pas les exécutions superflues.)

### 🧪 TP 0.2 — « Combien de requêtes ? »

*Énoncé.* Écrivez un petit programme qui, pour les 30 premiers biens de contrebande (triés par `Id`), affiche « rang, SKU, prix » **et** le total des prix à la fin. Une première version naïve utilise `q.Count()` dans la condition de boucle et `q.ElementAt(i)`. Mesurez-la, puis réécrivez-la pour qu'elle n'émette **qu'une requête**. Combien d'allers-retours gagnez-vous ?

<details><summary>Correction</summary>

```csharp
// Version naïve : 1 Count + 1 requête par élément à chaque tour → ~61 requêtes
await Mesurer("TP naïf", async db =>
{
    var q = db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30);
    decimal total = 0;
    for (var i = 0; i < await q.CountAsync(); i++)
    {
        var g = await q.Skip(i).FirstAsync();
        Console.WriteLine($"{i + 1,2} {g.Sku} {g.BasePrice}");
        total += g.BasePrice;
    }
    return total;
});

// Version corrigée : 1 requête
await Mesurer("TP corrigé", async db =>
{
    var biens = await db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30).ToListAsync();
    for (var i = 0; i < biens.Count; i++) Console.WriteLine($"{i + 1,2} {biens[i].Sku} {biens[i].BasePrice}");
    return biens.Sum(g => g.BasePrice);
});
// Gain : 61 → 1 requête (30 itérations × (Count + First) + le Count final de la condition).
```
</details>

---

## 0.3 Streaming vs buffering

**Axe concerné : 🧠 mémoire (+ ⚙️ .NET : GC) — et indirectement 🗄️ SQL (durée de vie de la requête).**

### La notion

- `ToList()` / `ToListAsync()` **bufferisent** : tout le résultat est en mémoire avant que votre code ne démarre.
- `foreach` / `await foreach (… AsAsyncEnumerable())` **streament** : une ligne à la fois, le *DataReader* reste ouvert pendant tout le traitement.
- Le **change tracker** peut annuler l'intérêt du streaming : en mode suivi (par défaut), chaque entité lue est **conservée**.

### Dans quels cas on est concerné

- Export, calcul d'agrégat, migration de données sur des centaines de milliers/millions de lignes.
- Services longue durée où la pression GC (génération 2) pose problème.
- Attention : un client **lent** garde la requête et le lecteur ouverts côté SQL Server.

### Exemple sans l'amélioration

```csharp
// ❌ 1 M d'entités en mémoire avant le premier calcul
await Mesurer("AVANT : ToListAsync", async db =>
{
    var tous = await db.Goods.AsNoTracking().ToListAsync();
    return tous.Sum(g => g.BasePrice);
});

// ❌ « Streaming » en trompe-l'œil : le tracking garde les 1 000 000 d'entités
await Mesurer("AVANT : foreach + tracking", async db =>
{
    decimal capital = 0;
    await foreach (var c in db.Companies.AsAsyncEnumerable()) capital += c.Capital;
    return $"capital={capital:N0} entités suivies={db.ChangeTracker.Entries().Count():N0}";
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Une ligne à la fois, sans suivi
await Mesurer("APRÈS : streaming", async db =>
{
    decimal somme = 0;
    await foreach (var g in db.Goods.AsNoTracking().AsAsyncEnumerable()) somme += g.BasePrice;
    return somme;
});

await Mesurer("APRÈS : foreach + AsNoTracking", async db =>
{
    decimal capital = 0;
    await foreach (var c in db.Companies.AsNoTracking().AsAsyncEnumerable()) capital += c.Capital;
    return $"capital={capital:N0} entités suivies={db.ChangeTracker.Entries().Count():N0}";
});

// ✔ Si le suivi est indispensable : le vider par lots
await Mesurer("APRÈS : tracking + Clear() par 10 000", async db =>
{
    decimal capital = 0; var i = 0;
    await foreach (var c in db.Companies.AsAsyncEnumerable())
    {
        capital += c.Capital;
        if (++i % 10_000 == 0) db.ChangeTracker.Clear();
    }
    return $"capital={capital:N0} entités suivies={db.ChangeTracker.Entries().Count():N0}";
});
```

**Ce qu'il faut observer**
- Avec le **pic de tas** (section D de la boîte à outils) ou `dotnet-counters` : `GC Heap Size` et `Gen 2 GC Count`.
- **Les allocations totales sont proches** entre `ToList` et streaming (chaque entité est quand même créée) ; ce qui change, c'est la mémoire **vivante** en même temps.
- Côté SQL : pendant l'exécution, la requête `SELECT ... FROM Goods` apparaît dans `sys.dm_exec_requests` pendant tout le streaming (voir section C.4).

### Sans index / avec index

Sans objet : on lit toute la table dans tous les cas.

### 🧪 TP 0.3 — « 1 M de sociétés, 100 Mo de budget »

*Énoncé.* Calculez le capital total et le capital moyen des 1 000 000 de sociétés (`Companies.Capital`) en mesurant le **pic du tas managé**. Partez d'une version qui charge les entités avec le tracking, puis ramenez le pic sous 100 Mo sans toucher au résultat. Dites quelle option vous choisissez entre « projection seule des colonnes utiles » et « streaming d'entités » et pourquoi.

<details><summary>Correction</summary>

```csharp
// Version de départ : tout en mémoire, avec tracking → pic très élevé
await Mesurer("TP départ", async db =>
{
    var l = await db.Companies.ToListAsync();
    return $"total={l.Sum(c => c.Capital):N0} moyenne={l.Average(c => c.Capital):N2}";
});

// Version corrigée : on ne lit que la colonne utile, en flux, sans suivi
await Mesurer("TP corrigé", async db =>
{
    decimal total = 0; long n = 0;
    await foreach (var capital in db.Companies.AsNoTracking().Select(c => c.Capital).AsAsyncEnumerable())
    { total += capital; n++; }
    return $"total={total:N0} moyenne={total / n:N2}";
});
// Choix : projection + streaming → pas d'entité du tout (juste un decimal par ligne), donc ni suivi ni objets lourds.
// Mieux encore : db.Companies.SumAsync / AverageAsync → SQL calcule, 1 ligne revient (voir 0.8).
```
</details>

---

## 0.4 Le pipeline EF Core

**Axe concerné : ⚙️ .NET (CPU, allocations) — peu d'effet sur SQL, sauf pour le cache de plans.**

### La notion

Pour chaque exécution, EF Core enchaîne : arbre d'expression → recherche dans son **cache de requêtes** (clé = forme de l'arbre) → pré-traitement/traduction (la 1ʳᵉ fois) → SQL → exécution → matérialisation (*shaper*) → suivi. Le coût .NET de cette chaîne est de l'ordre de dizaines de microsecondes par requête : négligeable devant une requête de plusieurs millisecondes, **significatif** pour des milliers de petites requêtes par seconde. Trois leviers :

| Levier | Effet |
|---|---|
| `EF.CompileQuery` / `CompileAsyncQuery` | évite le travail de pré-traitement à chaque appel |
| `AddDbContextPool` / `AddPooledDbContextFactory` | recycle les instances de `DbContext` (moins d'allocations) |
| **Paramètres plutôt que constantes** | une seule forme de requête, un seul plan SQL ; `EF.Constant(x)` ou une valeur écrite en dur dans l'arbre en crée une par valeur |

### Dans quels cas on est concerné

- *Hot path* : API appelée des milliers de fois par seconde, lectures par clé répétées.
- Valeurs injectées dans les requêtes (`EF.Constant`, `Expression.Constant`, SQL interpolé à la main) : **pollution du cache de plans SQL Server**.
- Un `DbContext` instancié par appel dans une boucle serrée.

### Exemple sans l'amélioration

```csharp
// ❌ Pipeline complet à chaque appel (2 000 lectures par clé)
await Mesurer("AVANT : requête normale ×2000", async db =>
{
    long s = 0;
    for (var i = 1; i <= 2000; i++) s += (await db.Goods.AsNoTracking().FirstOrDefaultAsync(g => g.Id == i))!.Id;
    return s;
});

// ❌ Une forme de requête par valeur (constante) → cache de requêtes EF et cache de plans SQL pollués
await Mesurer("AVANT : EF.Constant ×2000", async db =>
{
    long s = 0;
    for (var i = 1; i <= 2000; i++) { var id = i; s += (await db.Goods.AsNoTracking().Where(g => g.Id == EF.Constant(id)).FirstAsync()).Id; }
    return s;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Requête compilée : à déclarer dans une classe statique (en bas de Program.cs)
public static class Requetes
{
    public static readonly Func<StarWarsContext, int, Task<Good?>> GoodById =
        EF.CompileAsyncQuery((StarWarsContext c, int id) =>
            c.Goods.AsNoTracking().FirstOrDefault(g => g.Id == id));
}

await Mesurer("APRÈS : requête compilée ×2000", async db =>
{
    long s = 0;
    for (var i = 1; i <= 2000; i++) s += (await Requetes.GoodById(db, i))!.Id;
    return s;
});

// ✔ Variable capturée = paramètre SQL = une seule forme
await Mesurer("APRÈS : paramètre ×2000", async db =>
{
    long s = 0;
    for (var i = 1; i <= 2000; i++) { var id = i; s += (await db.Goods.AsNoTracking().Where(g => g.Id == id).FirstAsync()).Id; }
    return s;
});

// ✔ Contextes recyclés (nécessite : using Microsoft.Extensions.DependencyInjection;)
using var sp = new ServiceCollection()
    .AddPooledDbContextFactory<StarWarsContext>(o => o.UseSqlServer(Cs), poolSize: 64)
    .BuildServiceProvider();
var fabrique = sp.GetRequiredService<IDbContextFactory<StarWarsContext>>();
// … dans la boucle :  await using var c = await fabrique.CreateDbContextAsync(); …
```

> Attention : combiner une requête compilée statique et des contextes configurés différemment (autre fabrique, autres options) peut lever une exception à l'exécution. Testez la combinaison que vous retenez.

**Ce qu'il faut observer** : avec la boîte à outils, le **temps par appel** et les **allocations** (colonnes « Mo alloués ») ; l'écart est surtout visible dans les allocations, le SQL étant trivial. Pour le **cache de plans** :

```sql
DBCC FREEPROCCACHE;         -- avant le test (conteneur de test uniquement)
-- exécuter la boucle C# « EF.Constant », puis :
SELECT COUNT(*) AS plans FROM sys.dm_exec_cached_plans cp
CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
WHERE st.text LIKE '%FROM [[]Goods]%' AND st.text NOT LIKE '%dm_exec_cached_plans%';
-- recommencer avec la boucle « paramètre » : comparer le nombre de plans
```

### Comparaison avec une procédure stockée (même requête)

La procédure stockée évite la *traduction* côté SQL, mais **EF Core doit quand même** construire la commande et matérialiser le résultat. Créez :

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_GetGoodById @Id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Sku, Name, Category, BasePrice, WeightKg, IsContraband, IsPerishable
    FROM dbo.Goods WHERE Id = @Id;
END
```

```csharp
// Appel EF Core de la procédure (résultat NON composable : pas de .Where() après)
await Mesurer("PROC : EXEC Lab_GetGoodById ×2000", async db =>
{
    long s = 0;
    for (var i = 1; i <= 2000; i++)
    {
        var g = (await db.Goods.FromSql($"EXEC dbo.Lab_GetGoodById {i}").AsNoTracking().ToListAsync()).Single();
        s += g.Id;
    }
    return s;
});
```

| | LINQ (paramétré) | LINQ compilé | Procédure stockée |
|---|---|---|---|
| SQL côté serveur | requête paramétrée, plan en cache | idem | plan de la procédure en cache |
| Coût .NET | pipeline complet | réduit | construction de la commande + matérialisation |
| Composabilité | totale | totale | **aucune** (`EXEC` ne se compose pas) |
| Refactoring / typage | compilateur | compilateur | SQL en chaîne, à tester séparément |
| Droits / versionnement | dans le code | dans le code | objet de base à déployer |

**À observer** : les trois cas donnent le **même plan** (même `logical reads`) ; la différence se lit dans le **temps .NET et les allocations**. C'est le meilleur argument pour dire qu'une procédure stockée n'est pas, en soi, plus rapide qu'un LINQ bien écrit.

### Sans index / avec index

Les lectures par `Id` utilisent déjà l'index clustered (`Index Seek`). Pour voir l'effet **sans** index, faites la même boucle sur une colonne non indexée :

```csharp
// Sans index : Goods.Name n'est pas indexé → Scan à chaque appel (testez avec ×20, pas ×2000 !)
await Mesurer("Sans index (Name)", async db =>
{
    var n = 0;
    for (var i = 1; i <= 20; i++) { var nom = $"Kyber Durasteel"; n += await db.Goods.CountAsync(g => g.Name == nom); }
    return n;
});
```
```sql
CREATE INDEX IX_Lab_Goods_Name ON dbo.Goods (Name);       -- puis relancer la boucle : Seek / lecture d'un index étroit
DROP INDEX IX_Lab_Goods_Name ON dbo.Goods;
```
*Morale* : compiler une requête qui scanne 1 M de lignes ne la rend pas plus rapide ; l'index, si.

### 🧪 TP 0.4 — « Où va le temps ? »

*Énoncé.* Exécutez 2 000 lectures de `Goods` par `Id` en trois variantes : LINQ normal, LINQ compilé, procédure `Lab_GetGoodById`. Remplissez le tableau suivant à partir de vos mesures et concluez : *dans quel cas la compilation vaut-elle la peine ? et la procédure ?*

| Variante | Temps total | Temps / appel | Mo alloués | Logical reads / appel |
|---|---|---|---|---|

<details><summary>Correction</summary>

Méthode : lancer chaque variante **deux fois** (ignorer la 1ʳᵉ), et relever les *logical reads* d'**un** appel dans `sqlcmd` avec `SET STATISTICS IO ON` (`SELECT … WHERE Id = 12345` : quelques lectures, identiques pour les trois). Conclusion attendue : le SQL est identique dans les trois cas ; la compilation et la procédure ne grappillent que quelques dizaines de microsecondes et quelques Ko d'allocations par appel. Elles se justifient sur un *hot path* mesuré (milliers d'appels par seconde) ; ailleurs, la lisibilité et la composabilité de LINQ l'emportent. La procédure est surtout pertinente pour des raisons **hors performance** (droits d'accès, logique de base partagée entre applications).
</details>

---

## 0.5 Évaluation client

**Axe concerné : 🗄️ SQL (volume transféré) + 🧠 mémoire + ⚙️ .NET.**

### La notion

EF Core ne sait traduire qu'un sous-ensemble de C#. Règle actuelle :
- dans la **projection finale** (le dernier `Select`) : une méthode C# est exécutée **côté client**, après le SQL ;
- partout ailleurs (`Where`, `OrderBy`, `GroupBy`, `Join`…) : une expression non traduisible **lève une exception** ; l'issue « facile » est `AsEnumerable()` / `ToList()`, qui déplace **tout ce qui suit** en mémoire.

### Dans quels cas on est concerné

- Passer l'**entité entière** à une méthode dans un `Select` : EF doit ramener **toutes** les colonnes.
- `AsEnumerable()` placé trop tôt « pour que ça compile ».
- Prédicats avec `Regex`, `string.Format`, méthodes maison, `Split`, etc.

### Exemple sans l'amélioration

```csharp
static string Describe(Inhabitant i) => $"{i.FirstName} {i.LastName} ({i.BirthYear})";

// ❌ L'entité passe à la méthode : les 12 colonnes sont ramenées
await Mesurer("AVANT : Select(i => Describe(i))", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Take(200_000).Select(i => Describe(i)).ToListAsync()).Count,
    afficherSql: true);

// ❌ Prédicat non traduisible → AsEnumerable() : 1 M de biens rapatriés pour en garder 3 979
var rx = new System.Text.RegularExpressions.Regex("^Kyber .*steel$");
await Mesurer("AVANT : Regex côté client", db =>
    Task.FromResult(db.Goods.AsNoTracking().AsEnumerable().Count(g => rx.IsMatch(g.Name))));

// ❌ AsEnumerable() dès le départ : tous les filtres, même traduisibles, passent côté client
await Mesurer("AVANT : AsEnumerable() trop tôt", db =>
    Task.FromResult(db.Companies.AsNoTracking().AsEnumerable()
        .Count(c => c.Reputation > 95 && c.Name.Split(' ')[0].Length > 8)));
```

### Exemple avec l'amélioration

```csharp
static string Describe(string prenom, string nom, int annee) => $"{prenom} {nom} ({annee})";

// ✔ On ne ramène que 3 colonnes, le formatage se fait ensuite
await Mesurer("APRÈS : projection puis formatage", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Take(200_000)
        .Select(i => new { i.FirstName, i.LastName, i.BirthYear }).ToListAsync())
        .Select(x => Describe(x.FirstName, x.LastName, x.BirthYear)).Count(),
    afficherSql: true);

// ✔ Exprimer le critère avec une fonction traduite en SQL
await Mesurer("APRÈS : EF.Functions.Like", db =>
    db.Goods.CountAsync(g => EF.Functions.Like(g.Name, "Kyber %steel")), afficherSql: true);

// ✔ Filtrer côté SQL, n'évaluer côté client que le reste (et seulement la colonne utile)
await Mesurer("APRÈS : SQL puis client", async db =>
    (await db.Companies.AsNoTracking().Where(c => c.Reputation > 95).Select(c => c.Name).ToListAsync())
        .Count(nom => nom.Split(' ')[0].Length > 8));
```

**Ce qu'il faut observer** : la liste des colonnes du `SELECT` dans le journal SQL (12 → 3) ; « lignes lues » (1 M → quelques milliers) ; allocations.

### Sans index / avec index

Le `LIKE '%…'` (motif commençant par un joker) **ne peut pas** utiliser un index pour chercher ; un motif **préfixe** (`'Kyber %'`) le peut.

```csharp
await Mesurer("Préfixe : StartsWith", db => db.Goods.CountAsync(g => g.Name.StartsWith("Kyber ")), afficherSql: true);
```
```sql
SET STATISTICS IO ON;
-- 1) sans index sur Name : Clustered Index Scan de toute la table
-- 2) avec un index étroit : lecture d'une plage de l'index (beaucoup moins de pages)
CREATE INDEX IX_Lab_Goods_Name ON dbo.Goods (Name);
-- relancer le cas C#, comparer les logical reads et le plan
DROP INDEX IX_Lab_Goods_Name ON dbo.Goods;
```

### 🧪 TP 0.5 — « Combien de colonnes vraiment utiles ? »

*Énoncé.* On veut la liste des 1 000 premiers habitants (par `Id`) formatée `"NOM Prénom — planète n°X"`, avec le formatage dans une méthode C# `Formater(Inhabitant i)` existante. Écrivez la version qui ramène toutes les colonnes, puis la version qui n'en ramène que quatre. Comparez le SQL et la quantité de données transférées.

<details><summary>Correction</summary>

```csharp
static string Formater(Inhabitant i) => $"{i.LastName.ToUpper()} {i.FirstName} — planète n°{i.PlanetId}";
static string Formater(string nom, string prenom, int planete) => $"{nom.ToUpper()} {prenom} — planète n°{planete}";

await Mesurer("TP avant", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Take(1000).Select(i => Formater(i)).ToListAsync()).Count, afficherSql: true);

await Mesurer("TP après", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Take(1000)
        .Select(i => new { i.LastName, i.FirstName, i.PlanetId }).ToListAsync())
        .Select(x => Formater(x.LastName, x.FirstName, x.PlanetId)).Count(), afficherSql: true);
// Le journal SQL montre 12 colonnes (avant) contre 3 (après). Sur 1 000 lignes l'écart de temps est faible ;
// il grandit avec le nombre de lignes, la largeur des colonnes et la latence réseau.
```
</details>

---

## 0.6 Composition d'`IQueryable`

**Axe concerné : 🗄️ SQL (le filtre fusionne dans un seul SQL) + 🧠 mémoire.**

### La notion

Tant que l'on reste en `IQueryable`, chaque opérateur ajoute un nœud à l'arbre : `Where`, `OrderBy`, `Skip`, `Take`, `Select`… finissent en **une seule requête**. Dès qu'une couche appelle `ToList()`/`AsEnumerable()`/retourne `IEnumerable`/`List`, la composition s'arrête : la suite se passe en mémoire.

### Dans quels cas on est concerné

- Architecture en couches (accès aux données → service → contrôleur) où une couche basse matérialise.
- Pagination « Count + page » faite après tout chargement.
- Deux services enchaînés par une **liste d'identifiants** (`ids.Contains(...)`).
- Recherche à critères optionnels.

### Exemple sans l'amélioration

```csharp
// ❌ La couche basse matérialise (1,33 M d'habitants), la couche haute filtre en mémoire
static Task<List<Inhabitant>> ParGenre(StarWarsContext db, int genre) =>
    db.Inhabitants.AsNoTracking().Where(i => i.Gender == genre).OrderBy(i => i.Id).ToListAsync();

await Mesurer("AVANT : couche basse ToList", async db =>
{
    var tous = await ParGenre(db, 1);
    return tous.Where(i => i.IsActive && i.Credits > 200_000).Take(50).Count();
}, afficherSql: true);

// ❌ Pagination : tout charger, puis Count/Skip/Take en mémoire (Markets)
await Mesurer("AVANT : page en mémoire", async db =>
{
    var tous = await db.Markets.AsNoTracking().Where(m => m.Kind == 2 && !m.IsBlackMarket)
                       .OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).ToListAsync();
    return $"total={tous.Count} page={tous.Skip(100).Take(25).Count()}";
});

// ❌ Deux services chaînés par une liste d'ids
await Mesurer("AVANT : ids + Contains", async db =>
{
    var ids = await db.Markets.AsNoTracking().Where(m => m.IsBlackMarket && m.PlanetId < 300_000)
                      .Select(m => m.PlanetId).Distinct().ToListAsync();
    var l = await db.Planets.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Population > 50_000_000_000).ToListAsync();
    return $"{ids.Count} ids → {l.Count} planètes";
}, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ La couche basse reste composable
static IQueryable<Inhabitant> ParGenreQ(StarWarsContext db, int genre) =>
    db.Inhabitants.AsNoTracking().Where(i => i.Gender == genre).OrderBy(i => i.Id);

await Mesurer("APRÈS : requête composée", async db =>
    (await ParGenreQ(db, 1).Where(i => i.IsActive && i.Credits > 200_000).Take(50).ToListAsync()).Count, afficherSql: true);

// ✔ Count SQL + page SQL
await Mesurer("APRÈS : Count + Skip/Take SQL", async db =>
{
    var q = db.Markets.AsNoTracking().Where(m => m.Kind == 2 && !m.IsBlackMarket);
    var total = await q.CountAsync();
    var page  = await q.OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).Skip(100).Take(25).ToListAsync();
    return $"total={total} page={page.Count}";
}, afficherSql: true);

// ✔ Sous-requête composée (EXISTS) : une seule requête, aucune liste d'ids transférée
await Mesurer("APRÈS : sous-requête EXISTS", async db =>
{
    var noirs = db.Markets.Where(m => m.IsBlackMarket && m.PlanetId < 300_000);
    var l = await db.Planets.AsNoTracking()
        .Where(p => noirs.Any(m => m.PlanetId == p.Id) && p.Population > 50_000_000_000).ToListAsync();
    return l.Count;
}, afficherSql: true);
```

**Ce qu'il faut observer** : un seul `SELECT TOP(50) …` fusionnant les deux filtres ; la requête s'arrête dès 50 lignes trouvées ; « lignes lues » ≈ 50 au lieu de 1,33 M. Pour `Contains`, plus la liste d'ids est longue, plus le SQL généré grossit (voir aussi chapitre 2).

> **À vérifier vous-même — un mythe répandu** : le motif `Where(t => (goodId == null || t.GoodId == goodId) && …)` pour des critères optionnels. Comparez avec `ToQueryString()` le SQL qu'EF produit pour `goodId` nul et non nul, puis avec la version `if (goodId is not null) q = q.Where(...)`. Si le SQL est identique, la composition conditionnelle reste préférable pour la lisibilité, pas pour la performance.

### Sans index / avec index

La pagination `WHERE Kind = 2 AND IsBlackMarket = 0 ORDER BY OpenedOn, Id` trie 2 M de lignes filtrées. Un index aligné sur le filtre et sur l'ordre supprime le tri :

```sql
SET STATISTICS IO, TIME ON;
SELECT TOP (25) Id, PlanetId, Name, Kind, TaxRate, IsBlackMarket, OpenedOn
FROM dbo.Markets WHERE Kind = 2 AND IsBlackMarket = 0
ORDER BY OpenedOn, Id;                                   -- sans index : scan + Sort (voir le plan)
GO
CREATE INDEX IX_Lab_Markets_Kind_OpenedOn ON dbo.Markets (Kind, IsBlackMarket, OpenedOn, Id)
    INCLUDE (PlanetId, Name, TaxRate);
GO
SELECT TOP (25) Id, PlanetId, Name, Kind, TaxRate, IsBlackMarket, OpenedOn
FROM dbo.Markets WHERE Kind = 2 AND IsBlackMarket = 0
ORDER BY OpenedOn, Id;                                   -- Index Seek, pas de Sort
GO
DROP INDEX IX_Lab_Markets_Kind_OpenedOn ON dbo.Markets;
```
Refaites ensuite la variante C# « Count + Skip/Take » avant/après l'index et comparez.

### 🧪 TP 0.6 — « Une page de marchés noirs »

*Énoncé.* Écrivez une méthode `RechercherMarches(StarWarsContext db, int? kind, bool? noir, int page, int taille)` qui retourne le total et une page de marchés, **en n'émettant que deux requêtes**. Vérifiez avec `afficherSql: true`, puis montrez par `ToQueryString()` ce qui change dans le SQL quand `kind` et `noir` sont nuls.

<details><summary>Correction</summary>

```csharp
static async Task<(int total, List<Market> page)> RechercherMarches(StarWarsContext db, int? kind, bool? noir, int page, int taille)
{
    IQueryable<Market> q = db.Markets.AsNoTracking();
    if (kind is not null) q = q.Where(m => m.Kind == kind);
    if (noir is not null) q = q.Where(m => m.IsBlackMarket == noir);

    var total = await q.CountAsync();                                              // requête n°1
    var items = await q.OrderBy(m => m.OpenedOn).ThenBy(m => m.Id)
                       .Skip(page * taille).Take(taille).ToListAsync();            // requête n°2
    return (total, items);
}
// Avec kind = noir = null, le SQL ne contient aucun WHERE ; avec kind = 2 il contient WHERE [m].[Kind] = @kind.
```
Piège à signaler : `Skip` profond coûte de plus en plus cher (le serveur lit puis ignore les lignes) : voir la pagination par clé au chapitre 7.
</details>

---

## 0.7 Sémantique SQL vs C#

**Axe concerné : 🗄️ SQL (sargabilité) et justesse des résultats.**

### La notion

Le même LINQ peut avoir un **sens différent** en mémoire et en base, et EF Core fait de son mieux pour reproduire la sémantique C#, parfois au prix d'un SQL plus lourd.

| Sujet | En C# | En SQL |
|---|---|---|
| `NULL` | `null == null` est vrai | `NULL = NULL` est *inconnu* ; EF ajoute `OR … IS NULL` pour imiter C# |
| Casse | `==` sensible | dépend de la **collation** de la colonne (ici `SQL_Latin1_General_CP1_CI_AS` : insensible) |
| `ToLower()` sur une colonne | neutre | fonction appliquée à **chaque ligne** → l'index n'est plus utilisable |
| `Sum` de `int` | peut déborder silencieusement | `SUM(int)` renvoie un `int` : **erreur** de dépassement |
| Ordre sans `ORDER BY` | stable | **non garanti** |

### Dans quels cas on est concerné

- Recherches insensibles à la casse écrites avec `ToLower()`/`ToUpper()`.
- Comparaisons avec une colonne nullable (`!=`).
- Agrégats de grosses tables sur des types étroits (`int`).
- Pagination sans tri déterministe.

### Exemple sans l'amélioration

```csharp
// ❌ ToLower() sur la colonne : SQL doit calculer LOWER(Sku) pour chaque ligne → scan, alors que Sku est indexé
await Mesurer("AVANT : Sku.ToLower() == x (×20)", async db =>
{
    var trouves = 0;
    for (var i = 1; i <= 20; i++) trouves += await db.Goods.CountAsync(g => g.Sku.ToLower() == $"gd-{i * 37_000:D10}");
    return trouves;
}, afficherSql: false);

// ⚠ Même `!=` en C#… trois résultats possibles selon la sémantique
await Mesurer("C# naïf : != 7000", db => db.Inhabitants.CountAsync(i => i.MidiChlorians != 7000), afficherSql: true);

// ❌ SUM(int) : dépassement
await Mesurer("AVANT : Sum(Quantity * 2)", db => db.Trades.SumAsync(t => t.Quantity * 2));   // lève une erreur SQL de dépassement
```

### Exemple avec l'amélioration

```csharp
// ✔ La collation CI_AS rend déjà la comparaison insensible à la casse : l'égalité simple utilise l'index
await Mesurer("APRÈS : Sku == x (×20)", async db =>
{
    var trouves = 0;
    for (var i = 1; i <= 20; i++) trouves += await db.Goods.CountAsync(g => g.Sku == $"gd-{i * 37_000:D10}");
    return trouves;
});

// ✔ Exprimer l'intention nulle explicitement
await Mesurer("Explicite : != null && != 7000", db =>
    db.Inhabitants.CountAsync(i => i.MidiChlorians != null && i.MidiChlorians != 7000), afficherSql: true);

// ✔ Ou assumer la logique SQL pour tout le contexte (dans NewDb : .UseSqlServer(Cs, o => o.UseRelationalNulls()))

// ✔ Élargir le type avant d'agréger
await Mesurer("APRÈS : Sum((long)Quantity * 2)", db => db.Trades.SumAsync(t => (long)t.Quantity * 2));
```

**Ce qu'il faut observer**
- Le SQL de `!= 7000` : `WHERE [MidiChlorians] <> 7000 OR [MidiChlorians] IS NULL` (sémantique C#) ; **le résultat diffère** : environ 4 M de lignes contre environ 1 M.
- Le plan de `ToLower` : `Index Scan` (ou `Clustered Index Scan`) ; celui de l'égalité : `Index Seek` sur `IX_Goods_Sku` ou son équivalent unique.
- Pour voir la collation : `SELECT DATABASEPROPERTYEX('StarWarsTrade','Collation');`. Pour forcer une comparaison sensible à la casse : `EF.Functions.Collate(g.Sku, "Latin1_General_CS_AS") == "…"` — au prix d'un scan (l'index est trié en collation CI).

### Sans index / avec index

`Goods.Sku` possède un index unique (`Sku`) : la différence **seek / scan** vient donc de la façon d'écrire le prédicat (sargable ou non). **À vérifier dans le plan** : le **type du paramètre** peut aussi jouer (paramètre `nvarchar` sur une colonne `varchar` : voir 2.10). Vérifiez avec `SET STATISTICS IO ON` :

```sql
SET STATISTICS IO, TIME ON;
SELECT COUNT(*) FROM dbo.Goods WHERE LOWER(Sku) = 'gd-0000037000';   -- fonction sur la colonne : scan
SELECT COUNT(*) FROM dbo.Goods WHERE Sku = 'gd-0000037000';          -- sargable : seek
```

### 🧪 TP 0.7 — « Les trois `!=` »

*Énoncé.* Sur `Inhabitants.MidiChlorians` (nullable, ~75 % de `NULL`), écrivez les trois formes de « différent de 7000 » : C# naïve, explicite avec `!= null &&`, et avec `UseRelationalNulls`. Notez les trois résultats, le SQL de chacune, puis expliquez pourquoi les temps sont comparables alors que les résultats ne le sont pas.

<details><summary>Correction</summary>

```csharp
await Mesurer("naïf",      db => db.Inhabitants.CountAsync(i => i.MidiChlorians != 7000), afficherSql: true);              // ≈ 4 M
await Mesurer("explicite", db => db.Inhabitants.CountAsync(i => i.MidiChlorians != null && i.MidiChlorians != 7000), afficherSql: true); // ≈ 1 M

// UseRelationalNulls : dans NewDb(), .UseSqlServer(Cs, o => o.UseRelationalNulls())
// SQL : WHERE [MidiChlorians] <> 7000   → ≈ 1 M (les NULL sont exclus par la logique à trois valeurs)
```
Les trois requêtes parcourent la table entière (aucun index sur `MidiChlorians`), donc le coût est du même ordre ; seul le **résultat** change, d'où la gravité du piège : une régression de justesse, pas de performance. Avec un index sur `MidiChlorians`, le `OR … IS NULL` du cas naïf peut en revanche empêcher un `Seek` simple.
</details>

---

## 0.8 Les trois axes de mesure

**Axe concerné : tous — c'est la méthode.**

### La notion

| Axe | Ce qu'on regarde | Outil |
|---|---|---|
| ① CPU .NET | pipeline EF, délégués, complexité côté client | `Stopwatch`, BenchmarkDotNet |
| ② Mémoire | allocations, GC, pic du tas, taille du change tracker | `GC.GetTotalAllocatedBytes`, `dotnet-counters` |
| ③ SQL | logical reads, CPU et durée serveur, plan, allers-retours | `SET STATISTICS IO, TIME`, plan réel, DMV |

L'axe ③ domine presque toujours : **on optimise dans l'ordre ③ → ② → ①**. Un gain « CPU .NET » se compte en microsecondes, un gain SQL en dizaines à milliers de millisecondes.

### Dans quels cas on est concerné

À chaque optimisation. Les erreurs types : optimiser la compilation d'une requête qui scanne 6 M de lignes ; comparer un cache froid à un cache chaud ; conclure sans mesurer.

### Exemple sans l'amélioration

```csharp
// ❌ Le client fait le travail de SQL : top 5 des sociétés par valeur échangée en 2025
var depuis = new DateTime(2025, 1, 1);
await Mesurer("AVANT : GroupBy en C#", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.TradedAt >= depuis)
        .Select(t => new { t.CompanyId, t.Quantity, t.UnitPrice }).ToListAsync();
    var top = l.GroupBy(t => t.CompanyId).Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice) })
               .OrderByDescending(x => x.V).Take(5).ToList();
    return $"n°1 = société {top[0].Key} ({top[0].V:N0})";
});
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : GroupBy en SQL", async db =>
{
    var top = await db.Trades.Where(t => t.TradedAt >= depuis)
        .GroupBy(t => t.CompanyId)
        .Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice) })
        .OrderByDescending(x => x.V).Take(5).ToListAsync();
    return $"n°1 = société {top[0].Key} ({top[0].V:N0})";
}, afficherSql: true);
```

### Comparaison avec une procédure stockée équivalente

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_Top5Societes @Depuis datetime2
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (5) CompanyId, SUM(Quantity * UnitPrice) AS Valeur
    FROM dbo.Trades
    WHERE TradedAt >= @Depuis
    GROUP BY CompanyId
    ORDER BY SUM(Quantity * UnitPrice) DESC;
END
```
```csharp
public record Top5Ligne(int CompanyId, decimal Valeur);

await Mesurer("PROC : EXEC Lab_Top5Societes", async db =>
{
    var l = await db.Database.SqlQuery<Top5Ligne>($"EXEC dbo.Lab_Top5Societes {depuis}").ToListAsync();
    return $"n°1 = société {l[0].CompanyId} ({l[0].Valeur:N0})";
});
```
**À observer** : le SQL généré par EF pour le `GroupBy` (`afficherSql: true`) et le texte de la procédure sont **quasi identiques** ; dans `sqlcmd`, lancez les deux avec `SET STATISTICS IO, TIME ON` et comparez *logical reads* et plan. Le résultat attendu : **mêmes lectures, même plan**. La procédure n'apporte un avantage que si l'on peut l'optimiser autrement (hints, tables temporaires, logique multi-étapes) ou la partager entre applications ; LINQ garde l'avantage du typage et de la composabilité. Pour une comparaison complète de l'ensemble de ces choix, voir le chapitre 10.

### Sans index / avec index

Le `WHERE TradedAt >= …` n'a **aucun index** : SQL lit les 6 M de lignes. Index **couvrant** (clé = filtre, colonnes lues en `INCLUDE`) :

```sql
SET STATISTICS IO, TIME ON;
EXEC dbo.Lab_Top5Societes '2025-01-01';                         -- sans index : Clustered Index Scan complet
GO
CREATE INDEX IX_Lab_Trades_TradedAt_Cover ON dbo.Trades (TradedAt) INCLUDE (CompanyId, Quantity, UnitPrice);
GO
EXEC dbo.Lab_Top5Societes '2025-01-01';                         -- seek sur la plage 2025, lecture de l'index étroit
GO
DROP INDEX IX_Lab_Trades_TradedAt_Cover ON dbo.Trades;
```
Relancez ensuite les trois variantes C# (client, LINQ SQL, procédure) avec et sans l'index. Notez que **l'écart client/SQL reste énorme avec l'index** (le transfert de millions de lignes ne disparaît pas) tandis que **l'écart LINQ/procédure reste nul** : l'index améliore SQL, pas le choix de l'endroit où l'on calcule.

### Optimiser le bon axe : requête compilée **ou** index ?

```csharp
public static class Requetes2
{
    public static readonly Func<StarWarsContext, int, Task<int>> CompterQte =
        EF.CompileAsyncQuery((StarWarsContext c, int q) => c.Trades.Count(t => t.Quantity == q && t.TradeType == 1));
}

await Mesurer("RÉFÉRENCE : normale ×10", async db =>
{ var s = 0; for (var i = 0; i < 10; i++) s += await db.Trades.CountAsync(t => t.Quantity == 500 && t.TradeType == 1); return s; });

await Mesurer("① CPU : compilée ×10", async db =>
{ var s = 0; for (var i = 0; i < 10; i++) s += await Requetes2.CompterQte(db, 500); return s; });
```
```sql
CREATE INDEX IX_Lab_Trades_Qty_Type ON dbo.Trades (Quantity) INCLUDE (TradeType);   -- ③ SQL
-- relancer « RÉFÉRENCE » : même code C#, autre coût SQL
DROP INDEX IX_Lab_Trades_Qty_Type ON dbo.Trades;
```
**À observer** : la compilation ne change presque rien (le temps est dans le scan) ; l'index réduit les *logical reads* de plusieurs ordres de grandeur.

### Cache froid / cache chaud

```csharp
await Mesurer("Cache chaud", db => db.Planets.Where(p => p.HasSpaceport).SumAsync(p => p.Population));
```
```sql
CHECKPOINT; DBCC DROPCLEANBUFFERS;     -- puis relancer la ligne C# immédiatement : « Cache froid »
```
Comparez, via `SET STATISTICS IO`, **logical reads** (identiques) et **physical reads** (non nuls au froid). C'est pourquoi les *logical reads* servent de comparaison fiable, et pourquoi on précise toujours l'état du cache d'une mesure.

### 🧪 TP 0.8 — « Un tableau de décision »

*Énoncé.* Pour la requête « top 5 des sociétés par valeur échangée en 2025 », remplissez le tableau suivant en mesurant les variantes (client / LINQ SQL / procédure) **sans puis avec** l'index `IX_Lab_Trades_TradedAt_Cover`, et rédigez trois phrases : quel axe domine ? quelle optimisation apporte le plus ? la procédure stockée apporte-t-elle quelque chose ici ?

| Variante | Index | Temps C# | Mo alloués | Requêtes | Lignes lues | Logical reads | CPU SQL | Durée SQL |
|---|---|---|---|---|---|---|---|---|

<details><summary>Correction</summary>

Méthode : pour les colonnes SQL, exécuter la requête (ou la procédure) dans `sqlcmd` avec `SET STATISTICS IO, TIME ON` ; pour les colonnes .NET, lire la ligne de `Mesurer`. Ce que le tableau doit faire apparaître :
1. **Axe dominant** : le transfert et la matérialisation des ~2 M de lignes (variante « client ») coûtent bien plus que n'importe quel réglage du pipeline EF ; l'axe ③ (SQL + volume de données) domine.
2. **Meilleur gain** : déplacer le `GroupBy` côté SQL (lignes lues : 2 M → 5) ; l'index apporte ensuite un gain supplémentaire sur les *logical reads* de la requête SQL, sans effet sur la variante « client » qui paie toujours le transfert.
3. **Procédure stockée** : même plan et mêmes lectures que le LINQ ; pas de gain de performance en soi, uniquement des raisons organisationnelles (droits, partage, logique multi-étapes).
</details>

---

## Script de nettoyage

À exécuter à la fin de vos essais (idempotent) pour retrouver la base d'origine :

```sql
DROP INDEX IF EXISTS IX_Lab_Goods_Contraband        ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Goods_Name              ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Markets_Kind_OpenedOn   ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt_Cover         ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Qty_Type              ON dbo.Trades;
DROP PROCEDURE IF EXISTS dbo.Lab_GetGoodById;
DROP PROCEDURE IF EXISTS dbo.Lab_Top5Societes;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
