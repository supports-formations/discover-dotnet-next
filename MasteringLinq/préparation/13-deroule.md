# 13 — Déroulé du workshop (5 heures, autonome)

> Ce déroulé **répartit la matière sur 5 heures** pour un public **très expérimenté**. **Il est autonome** : chaque démonstration contient directement le code **« avant »** (sans l'amélioration) et **« après »** (avec), le SQL des index éventuels et ce qu'il faut observer. Les renvois (« module 07, 7.3.1 ») ne servent qu'à **approfondir**.
> Les modules [00](00-prerequis.md) à [12](12-pieges.md) contiennent **beaucoup plus** de matière que 5 heures n'en permettent : ce document dit **quoi traiter en séance**, **quoi donner en autonomie**, **quoi supprimer si l'on prend du retard**. Version courte : [14](14-workshop-2h.md) ; FAQ : [15](15-faq.md).
> **Avertissement important** : les extraits ont été écrits **sans être exécutés**. **Une répétition générale complète est obligatoire** (compilation, résultats, durées). Les durées sont des **estimations**.

## Rappel des besoins et couverture

| Besoin | Blocs |
|---|---|
| **B1** Éviter de quitter EF Core pour un autre ORM | 1, 3, 6 |
| **B2** Techniques simples | 0, 1 |
| **B3** Techniques avancées | 2, 3 |
| **B4** Millions de lignes | 4 |
| **B5** Nouveautés (bundle, split queries…) | 2, 4, 5 |
| **B6** Quand quitter LINQ, ORM alternatifs, LINQ contre procédure | 6 |

## Vue d'ensemble

| Bloc | Durée | Horaire | Thème |
|---|--:|---|---|
| 0 | 25 min | 0:00 – 0:25 | Cadre, outils de mesure, modèle mental |
| 1 | 45 min | 0:25 – 1:10 | Lire et corriger une requête : techniques simples |
| 2 | 40 min | 1:10 – 1:50 | Données liées et suivi des changements |
| — | 15 min | 1:50 – 2:05 | **Pause** |
| 3 | 50 min | 2:05 – 2:55 | Techniques avancées |
| 4 | 50 min | 2:55 – 3:45 | Millions de lignes |
| — | 10 min | 3:45 – 3:55 | **Pause** |
| 5 | 15 min | 3:55 – 4:10 | Nouveautés d'EF Core |
| 6 | 30 min | 4:10 – 4:40 | Sortir de LINQ, LINQ contre procédure stockée |
| 7 | 20 min | 4:40 – 5:00 | Pièges, garde-fous, synthèse |

**Rythme de chaque démonstration** : *prédiction* (« combien de requêtes ? de lignes ? seek ou scan ? ») → exécution de **« avant »** → exécution de **« après »** → comparaison → TP court → correction. On **mesure toujours** avant d'affirmer (SQL → mémoire → CPU).

---

## Préparation du formateur

### Sept jours avant
- [ ] **Exécuter tous les extraits** que vous présenterez, **dans l'ordre**, sur la machine et la base de l'atelier ; noter durée réelle, résultats, erreurs.
- [ ] Vérifier les points *(à vérifier)* des modules : 9.8 (signature de `IgnoreQueryFilters`), 9.11 (**EF Core 11 : lire « What's new in EF Core 11 »**), 10 (API Dapper/linq2db), 2.10 (conversion implicite et collation).
- [ ] Trancher avec les organisateurs le sens de **« bundler de requête »** (5.8, 9.12).
- [ ] Décider si une instance **SQL Server 2025** est disponible pour la démonstration vectorielle (9.10) ; sinon la présenter en lecture.
- [ ] Préparer un **projet de secours** qui compile.

### La veille
- [ ] `docker compose up -d` ; base chargée (38 M de lignes) ; `SELECT COUNT(*)` par table.
- [ ] **Réchauffer le cache** (une lecture de `Trades`, `Inhabitants`, `Planets`, `Goods`).
- [ ] **Créer à l'avance** les structures lourdes : l'index *columnstore* (bloc 4) — gardez le script de suppression.
- [ ] Contrôler la mémoire du conteneur (`docker stats`) et le disque.

### Une heure avant
- [ ] Le squelette ci-dessous compile pour tout le monde ; chacun lance `dotnet run -c Release` une fois.
- [ ] Règle de sécurité rappelée : écritures **annulées** ou tables `Lab_*` ; objets `IX_Lab_` / `Lab_` ; on ne modifie jamais les tables d'origine.

---

## Squelette commun (à coller dans `Program.cs`)

> Instructions de haut niveau d'abord, déclarations de types **en bas du fichier**. Paquet `Dapper` nécessaire pour le bloc 6.

```csharp
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using MasteringLinq.Data;
using MasteringLinq.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

const string Cs = "Server=localhost,1433;Database=StarWarsTrade;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False;Connect Timeout=15";
var compteur = new CompteurSql();

StarWarsContext NewDb(bool afficherSql = false)
{
    var b = new DbContextOptionsBuilder<StarWarsContext>().UseSqlServer(Cs, o => o.CommandTimeout(300)).AddInterceptors(compteur, new Indices());
    if (afficherSql)
        b.LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).EnableSensitiveDataLogging();
    return new StarWarsContext(b.Options);
}

async Task<T> Mesurer<T>(string nom, Func<StarWarsContext, Task<T>> action, bool afficherSql = false)
{
    await using var db = NewDb(afficherSql);
    compteur.Reset();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long a0 = GC.GetTotalAllocatedBytes(true); int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
    var sw = Stopwatch.StartNew();
    var r = await action(db);
    sw.Stop();
    Console.WriteLine($"{nom,-52} {sw.ElapsedMilliseconds,7:N0} ms | {(GC.GetTotalAllocatedBytes(true) - a0) / 1_048_576.0,8:N1} Mo alloués" +
                      $" | GC0:{GC.CollectionCount(0) - g0} GC2:{GC.CollectionCount(2) - g2} | SQL: {compteur.Requetes} req, {compteur.Lignes:N0} lignes | résultat: {r}");
    return r;
}

async Task<T> MesurerReseau<T>(string nom, Func<StarWarsContext, Task<T>> action)
{
    await using var db = NewDb();
    await db.Database.OpenConnectionAsync();
    var cn = (SqlConnection)db.Database.GetDbConnection();
    cn.StatisticsEnabled = true; cn.ResetStatistics(); compteur.Reset();
    var sw = Stopwatch.StartNew(); var r = await action(db); sw.Stop();
    var s = cn.RetrieveStatistics();
    Console.WriteLine($"{nom,-52} {sw.ElapsedMilliseconds,7:N0} ms | SQL: {compteur.Requetes} req, {compteur.Lignes:N0} lignes | {Convert.ToInt64(s["BytesReceived"]) / 1_048_576.0,7:N1} Mo reçus | {r}");
    return r;
}

async Task<T> DansTransactionAnnulee<T>(StarWarsContext db, Func<Task<T>> action)
{
    await using var tx = await db.Database.BeginTransactionAsync();
    try { return await action(); } finally { await tx.RollbackAsync(); }
}

async Task<T> AvecMaxRequetes<T>(int max, Func<Task<T>> action)
{
    compteur.Reset(); var r = await action();
    if (compteur.Requetes > max) throw new InvalidOperationException($"{compteur.Requetes} requêtes (max {max}) : N+1 probable");
    return r;
}

// ───── les démonstrations ci-dessous ─────

public sealed class CompteurSql : DbCommandInterceptor
{
    public int Requetes; public long Lignes;
    public void Reset() { Requetes = 0; Lignes = 0; }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r) { Requetes++; return r; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Requetes++; return new(r); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r) { Requetes++; return r; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Requetes++; return new(r); }
    public override InterceptionResult DataReaderDisposing(DbCommand c, DataReaderDisposingEventData e, InterceptionResult r) { Lignes += e.ReadCount; return r; }
}

// Réagit à TagWith("recompile") / TagWith("unknown") : ajoute un hint de requête (bloc 3)
public sealed class Indices : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r) { Modifier(c); return r; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Modifier(c); return new(r); }
    private static void Modifier(DbCommand c)
    {
        if (c.CommandText.StartsWith("-- recompile", StringComparison.Ordinal)) c.CommandText += " OPTION (RECOMPILE)";
        else if (c.CommandText.StartsWith("-- unknown", StringComparison.Ordinal)) c.CommandText += " OPTION (OPTIMIZE FOR UNKNOWN)";
    }
}

public static class Compilees
{
    public static readonly Func<StarWarsContext, int, Task<int>> CompterQte =
        EF.CompileAsyncQuery((StarWarsContext c, int q) => c.Trades.Count(t => t.Quantity == q && t.TradeType == 1));
}
public record TopLigne(int GoodId, long Id, decimal UnitPrice);
```
**Côté SQL** : `docker exec -it masteringlinq-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'StarWars_Pwd123!' -d StarWarsTrade` (ou SSMS/Azure Data Studio) ; `SET STATISTICS IO, TIME ON;` pour lire `logical reads` ; plan réel (Ctrl+M) ou `SET STATISTICS XML ON;`.

---

## Bloc 0 — Cadre, outils de mesure, modèle mental (25 min)

| Min | Activité |
|--:|---|
| 0–5 | Présentation, carte des besoins, règles de sécurité |
| 5–12 | Boîte à outils (échauffement ci-dessous) |
| 12–20 | **Démo 0.1** : `Func` contre `Expression` |
| 20–25 | **Démo 0.8** : qui fait le travail ? (client contre SQL) |

### Échauffement (7 min)
```csharp
await Mesurer("échauffement : Count des biens", db => db.Goods.CountAsync(), afficherSql: true);
Console.WriteLine(NewDb().Goods.Where(g => g.IsContraband).ToQueryString());                  // le SQL, sans l'envoyer
```
**Message** : *« On ne devine pas. On lit le SQL et on compte les lectures. »*

### Démo 0.1 — `Func` contre `Expression` (8 min)
**Avant :**
```csharp
await Mesurer("AVANT : Func", db =>
{
    Func<Good, bool> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return Task.FromResult(db.Goods.AsNoTracking().Where(contrebande).ToList().Count);        // Enumerable.Where : tout est lu puis filtré en C#
}, afficherSql: true);
```
**Après :**
```csharp
await Mesurer("APRÈS : Expression", async db =>
{
    System.Linq.Expressions.Expression<Func<Good, bool>> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return (await db.Goods.AsNoTracking().Where(contrebande).ToListAsync()).Count;
}, afficherSql: true);
```
**À observer** : `SELECT` sans `WHERE`, ~1 M de lignes lues, centaines de Mo alloués, contre une poignée de lignes. **Le piège réel** : une signature de dépôt.
```csharp
// ❌ AVANT
public List<Good> Where(Func<Good, bool> p) => db.Goods.AsNoTracking().Where(p).ToList();
// ✔ APRÈS
public List<Good> Where(Expression<Func<Good, bool>> p) => db.Goods.AsNoTracking().Where(p).ToList();
// l'appelant écrit la même chose dans les deux cas
```
**TP (5 min, autonomie)** : corriger ce dépôt. <details><summary>Correction</summary>
```csharp
public class GoodRepository(StarWarsContext db)
{
    public IQueryable<Good> GetAll() => db.Goods.AsNoTracking();                                      // au lieu de IEnumerable<Good>
    public List<Good> Where(System.Linq.Expressions.Expression<Func<Good, bool>> p) => db.Goods.AsNoTracking().Where(p).ToList();
}
```
</details>

### Démo 0.8 — Top 5 des sociétés : qui fait le travail ? (5 min de démo, 2 s de calcul à lire)
**Avant — le client fait le `GroupBy` de 2 M de lignes :**
```csharp
var depuis = new DateTime(2025, 1, 1);
await Mesurer("AVANT : GroupBy en C#", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.TradedAt >= depuis).Select(t => new { t.CompanyId, t.Quantity, t.UnitPrice }).ToListAsync();
    var top = l.GroupBy(t => t.CompanyId).Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice) }).OrderByDescending(x => x.V).Take(5).ToList();
    return $"n°1 = société {top[0].Key} ({top[0].V:N0})";
});
```
**Après — SQL fait le `GroupBy`, le tri et le `TOP 5` :**
```csharp
await Mesurer("APRÈS : GroupBy en SQL", async db =>
{
    var top = await db.Trades.Where(t => t.TradedAt >= depuis).GroupBy(t => t.CompanyId)
        .Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice) }).OrderByDescending(x => x.V).Take(5).ToListAsync();
    return $"n°1 = société {top[0].Key} ({top[0].V:N0})";
}, afficherSql: true);
```
**À observer** : lignes transférées (≈ 2 M → 5) ; allocations ; *logical reads* **identiques** (un scan dans les deux cas) : on gagne sur le **transfert**, pas sur le disque. **L'ordre de travail est SQL → mémoire → CPU.**

**Option si le temps le permet — optimiser le bon axe (compilée contre index)** :
```csharp
await Mesurer("RÉFÉRENCE : normale ×10", async db => { var s = 0; for (var i = 0; i < 10; i++) s += await db.Trades.CountAsync(t => t.Quantity == 500 && t.TradeType == 1); return s; });
await Mesurer("① CPU : compilée ×10",     async db => { var s = 0; for (var i = 0; i < 10; i++) s += await Compilees.CompterQte(db, 500); return s; });
```
```sql
CREATE INDEX IX_Lab_Trades_Qty_Type ON dbo.Trades (Quantity) INCLUDE (TradeType);        -- ③ SQL : puis relancer « RÉFÉRENCE »
DROP INDEX IX_Lab_Trades_Qty_Type ON dbo.Trades;
```
La compilation ne change presque rien ; l'index divise les lectures de plusieurs ordres de grandeur.

- **À donner en autonomie** : modules 0.2 à 0.7.
- **Point de contrôle** : chacun a obtenu **une ligne `Mesurer`** et lu **un plan réel**.

---

## Bloc 1 — Lire et corriger une requête : techniques simples (45 min)

| Min | Activité |
|--:|---|
| 0–10 | Projection et suivi (démo 1.1) |
| 10–18 | `Any` contre `Count`, pagination bornée (démos 1.2, 1.3) |
| 18–34 | Sargabilité + index, **sans puis avec** (démo 1.4) |
| 34–38 | Lecture de plan |
| 38–45 | **TP** « Corriger cinq prédicats » |

### Démo 1.1 — Projection, volume transféré, suivi (10 min)
**Avant :**
```csharp
await MesurerReseau("AVANT : entité complète, suivie", async db =>
{
    var l = await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).ToListAsync();
    return $"{l.Count:N0} lignes, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**Après :**
```csharp
await MesurerReseau("APRÈS : projection", async db =>
{
    var l = await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).Select(s => new { s.Name, s.Model }).ToListAsync();
    return $"{l.Count:N0} lignes, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**Le piège — une entité glissée dans la projection est suivie :**
```csharp
await Mesurer("PIÈGE : entité dans la projection", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 200_000).Select(g => new { Bien = g, Taxe = g.BasePrice * 0.2m }).ToListAsync();
    return $"suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**Index couvrant (sans puis avec)** — la projection est la condition de l'index :
```sql
SET STATISTICS IO, TIME ON;
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;       -- Seek + Key Lookup
CREATE INDEX IX_Lab_Starships_Cover ON dbo.Starships (CompanyId) INCLUDE (IsOperational, Name, Model);
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;       -- Seek seul
DROP INDEX IX_Lab_Starships_Cover ON dbo.Starships;
```
**À observer** : colonnes du `SELECT` (8 → 2) ; Mo reçus ; entités suivies (≈ 40 000 → 0).

### Démo 1.2 — `Any` contre `Count() > 0` (4 min)
Calibrage : `Console.WriteLine(await NewDb().Starships.MinAsync(s => s.CargoCapacity) + " .. " + await NewDb().Starships.MaxAsync(s => s.CargoCapacity));`
```csharp
// AVANT
await Mesurer("AVANT : Count() > 0", async db => await db.Starships.CountAsync(s => s.CargoCapacity > 100) > 0, afficherSql: true);
// APRÈS
await Mesurer("APRÈS : Any", db => db.Starships.AnyAsync(s => s.CargoCapacity > 100), afficherSql: true);
await Mesurer("Any sans résultat", db => db.Starships.AnyAsync(s => s.CargoCapacity > 999_999_999), afficherSql: true);       // le cas défavorable
```
```sql
CREATE INDEX IX_Lab_Starships_Cargo ON dbo.Starships (CargoCapacity);        -- « Any sans résultat » devient un seek
DROP INDEX IX_Lab_Starships_Cargo ON dbo.Starships;
```

### Démo 1.3 — Pagination bornée et tri déterministe (4 min)
```csharp
// AVANT : aucune borne, tri non déterministe, offset profond
await Mesurer("AVANT : Take sans OrderBy", async db => (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).Take(50).ToListAsync()).Count, afficherSql: true);
await Mesurer("AVANT : Skip(3 000 000)", async db => (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Skip(3_000_000).Take(50).ToListAsync()).Count, afficherSql: true);
// APRÈS : clé unique en dernier critère, borne, position connue
await Mesurer("APRÈS : tri déterministe + borne", async db => (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).Take(50).ToListAsync()).Count, afficherSql: true);
await Mesurer("APRÈS : keyset (Id > 3 000 000)", async db => (await db.Inhabitants.AsNoTracking().Where(i => i.Id > 3_000_000).OrderBy(i => i.Id).Take(50).ToListAsync()).Count, afficherSql: true);
```

### Démo 1.4 — Sargabilité + index : le couple indissociable (16 min)
**Avant :**
```csharp
await Mesurer("AVANT : Sku.ToLower() ×20", async db =>
{ var n = 0; for (var i = 1; i <= 20; i++) n += await db.Goods.CountAsync(g => g.Sku.ToLower() == $"gd-{i * 37_000:D10}"); return n; });
await Mesurer("AVANT : Year/Month sur la colonne", db => db.Trades.CountAsync(t => t.TradedAt.Year == 2025 && t.TradedAt.Month == 3), afficherSql: true);
```
**Après :**
```csharp
await Mesurer("APRÈS : Sku == x ×20", async db =>
{ var n = 0; for (var i = 1; i <= 20; i++) n += await db.Goods.CountAsync(g => g.Sku == $"gd-{i * 37_000:D10}"); return n; });       // collation CI_AS : insensible à la casse
var debut = new DateTime(2025, 3, 1); var fin = debut.AddMonths(1);
await Mesurer("APRÈS : intervalle de dates", db => db.Trades.CountAsync(t => t.TradedAt >= debut && t.TradedAt < fin), afficherSql: true);
```
**Sans puis avec index :**
```sql
SET STATISTICS IO, TIME ON;
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                          -- scan
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                     -- scan aussi (pas d'index)
CREATE INDEX IX_Lab_Trades_TradedAt ON dbo.Trades (TradedAt);
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                          -- Index Scan (tout l'index)
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                     -- Index Seek (une plage)
DROP INDEX IX_Lab_Trades_TradedAt ON dbo.Trades;
```
**À observer** : sans index, les deux scannent ; avec index, seule l'écriture **sargable** fait un *Seek* ; les *logical reads* chutent de plusieurs ordres de grandeur ; sur `Sku`, vérifiez dans le plan si un paramètre `nvarchar` sur colonne `varchar` joue (module 02, 2.10). **Message** : *« Il faut les deux. »*

### Lecture de plan (4 min)
Sur le plan réel de « intervalle de dates » (avec index) : opérateur d'accès, **lignes estimées / réelles**, absence de *Key Lookup*, avertissements.

### TP (7 min) — « Corriger cinq prédicats »
Réécrivez de façon sargable : `t.TradedAt.Year == 2024` ; `i.BirthYear + 30 > 2000` ; `g.Sku.Substring(0, 3) == "GD-"` ; `g.Name.ToUpper() == "KYBER DURASTEEL"` ; `s.CargoCapacity / 10 > 500`.
<details><summary>Correction</summary>

`t.TradedAt >= new DateTime(2024,1,1) && t.TradedAt < new DateTime(2025,1,1)` ; `i.BirthYear > 1970` ; `g.Sku.StartsWith("GD-")` ; `g.Name == "kyber durasteel"` ; `s.CargoCapacity > 5_000` (vérifier la division entière dans le SQL). `ToQueryString()` avant/après.
</details>

- **À donner en autonomie** : modules 01 (1.4 à 1.9), 02, 06 (6.2, 6.3, 6.6, 6.9 à 6.14).
- **Question de relance** : *« Dans quel cas un `Any` est-il aussi lent qu'un `Count` ? »* (aucune ligne, sans index.)

---

## Bloc 2 — Données liées et suivi des changements (40 min)

| Min | Activité |
|--:|---|
| 0–12 | Explosion cartésienne (démo 2.1) |
| 12–20 | N+1 et budget de requêtes (démo 2.2) |
| 20–32 | Suivi : `AsNoTracking`, défaut global, piège `SaveChanges` = 0 (démo 2.3) |
| 32–40 | **TP** |

### Démo 2.1 — Explosion cartésienne (12 min)
**Prédiction** : un bien a ~6 échanges, ~4 `MarketGoods`, ~3 cargos → ≈ 72 lignes par bien.
**Avant :**
```csharp
await MesurerReseau("AVANT : single query, 3 collections", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSingleQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);
```
**Après (1) — split query :**
```csharp
await MesurerReseau("APRÈS (1) : AsSplitQuery", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSplitQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);
```
**Après (2) — projection :**
```csharp
await MesurerReseau("APRÈS (2) : projection", async db =>
    (await db.Goods.Where(g => g.Id <= 2_000)
        .Select(g => new { g.Sku, Echanges = g.Trades.Count(), Marches = g.MarketGoods.Count(), Cargos = g.StarshipCargos.Count() }).ToListAsync()).Count);
```
**À observer** : lignes **produit** (≈ 144 000) contre **somme** (≈ 26 000) contre 2 000 ; Mo reçus ; avertissement d'EF ; 1 requête → 4. **Contrepartie du split** : lecture non atomique (module 03, 3.9).

**Variante — `Include` filtré** (les 5 derniers échanges de chaque marché) :
```csharp
await MesurerReseau("AVANT : Include complet", async db =>
{
    var l = await db.Markets.AsNoTracking().Where(m => m.Id <= 2_000).Include(m => m.Trades).ToListAsync();
    return l.Sum(m => m.Trades.OrderByDescending(t => t.TradedAt).Take(5).Count());
});
await MesurerReseau("APRÈS : Include filtré", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Id <= 2_000).Include(m => m.Trades.OrderByDescending(t => t.TradedAt).Take(5)).ToListAsync()).Sum(m => m.Trades.Count));
```
```sql
CREATE INDEX IX_Lab_Trades_Market_Date ON dbo.Trades (MarketId, TradedAt DESC);       -- supprime le tri par marché
DROP INDEX IX_Lab_Trades_Market_Date ON dbo.Trades;
```

### Démo 2.2 — N+1 et budget de requêtes (8 min)
**Avant :**
```csharp
await Mesurer("AVANT : N+1", async db =>
{
    var societes = await db.Companies.AsNoTracking().Where(c => c.Id <= 1_000).ToListAsync();
    var total = 0; foreach (var c in societes) total += await db.Trades.CountAsync(t => t.CompanyId == c.Id);       // 1 requête par société
    return total;
});
```
**Après :**
```csharp
await Mesurer("APRÈS : GroupBy SQL", async db =>
    (await db.Trades.Where(t => t.CompanyId <= 1_000).GroupBy(t => t.CompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Sum(x => x.N));
// Le garde-fou : échoue dès qu'une boucle revient
await AvecMaxRequetes(2, async () => await Mesurer("garde-fou", async db => (await db.Trades.Where(t => t.CompanyId <= 1_000).GroupBy(t => t.CompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Count));
```
**À observer** : 1 001 requêtes → 1. Un N+1 se **détecte par un compteur**, pas à l'œil.

### Démo 2.3 — Suivi : `AsNoTracking`, défaut global, piège (12 min)
**Avant — 1 M d'entités suivies pour un simple calcul :**
```csharp
await Mesurer("AVANT : avec suivi", async db =>
{
    var l = await db.Goods.ToListAsync();
    return $"{l.Count:N0} biens, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**Après :**
```csharp
await Mesurer("APRÈS : AsNoTracking", async db =>
{
    var l = await db.Goods.AsNoTracking().ToListAsync();
    return $"{l.Count:N0} biens, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**Le piège de `AsNoTracking` : `SaveChanges` renvoie 0 sans erreur** (transaction annulée, rien n'est écrit de toute façon) :
```csharp
await Mesurer("PIÈGE : modifier une entité non suivie", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var g = await db.Goods.AsNoTracking().FirstAsync(x => x.Id == 42);
        g.BasePrice *= 1.1m;
        return $"lignes enregistrées : {await db.SaveChangesAsync()}";                  // 0 : rien n'est suivi, aucune erreur
    }));
```
**Modifier sans lire** (une seule colonne dans l'`UPDATE`) :
```csharp
await Mesurer("Attach + IsModified", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var g = new Good { Id = 42, Sku = "", Name = "", BasePrice = 124.45m };
        db.Attach(g); db.Entry(g).Property(x => x.BasePrice).IsModified = true;
        return await db.SaveChangesAsync();
    }), afficherSql: true);
```
**À observer** : entités suivies (1 000 000 → 0) ; Mo alloués ; GC2.

### TP (8 min) — « Mesurer l'explosion »
Pour `Goods` d'`Id ≤ 2 000`, faites varier le nombre de collections incluses (1, 2, 3) en *single* puis en *split* et remplissez : collections / lignes lues / Mo reçus / temps.
<details><summary>Correction</summary>

En *single*, le nombre de lignes suit le **produit** des cardinalités (≈ 6, ≈ 24, ≈ 72 par bien) ; en *split*, la **somme** (≈ 6, ≈ 10, ≈ 13). Avec **une** collection, le split n'économise rien (et ajoute un aller-retour).
</details>

- **À donner en autonomie** : 3.1, 3.3, 3.4, 3.5 (lazy loading), 3.9, 3.11 à 3.13, 4.2, 4.3, 4.5, 4.7.

---

## Pause (15 min)

---

## Bloc 3 — Techniques avancées (50 min)

| Min | Activité |
|--:|---|
| 0–12 | *Parameter sniffing* et stratégies (démo 3.1) |
| 12–22 | Dessiner un index (démo 3.2) |
| 22–34 | Pagination par clé (démo 3.3) |
| 34–44 | Top N par groupe (démo 3.4) |
| 44–50 | **TP** |

### Démo 3.1 — Le même code, deux plans (12 min)
```sql
CREATE INDEX IX_Lab_Planets_Population ON dbo.Planets (Population);          -- SANS INCLUDE : lookups pour les autres colonnes
DBCC FREEPROCCACHE;                                                          -- conteneur de test uniquement
```
**Avant :**
```csharp
long etroit = 99_000_000_000, large = 1_000_000_000;                          // calibrez avec COUNT : ~1 000 lignes contre plusieurs centaines de milliers
IQueryable<Planet> Q(StarWarsContext db, long seuil, string tag) => db.Planets.TagWith(tag).AsNoTracking().Where(p => p.Population > seuil);

await Mesurer("standard : étroit puis large", async db =>
{
    var a = (await Q(db, etroit, "standard").ToListAsync()).Count;            // plan « seek + lookups » compilé pour cette valeur…
    var b = (await Q(db, large, "standard").ToListAsync()).Count;             // …réutilisé pour une valeur large
    return $"{a} + {b}";
});
```
**Après — `OPTION (RECOMPILE)` par intercepteur (le tag `recompile` déclenche l'intercepteur `Indices` du squelette) :**
```csharp
await Mesurer("recompile : étroit puis large", async db =>
{
    var a = (await Q(db, etroit, "recompile").ToListAsync()).Count;
    var b = (await Q(db, large, "recompile").ToListAsync()).Count;
    return $"{a} + {b}";
}, afficherSql: true);
// Autres remèdes : tag "unknown" (OPTIMIZE FOR UNKNOWN) ; .Where(p => p.Population > EF.Constant(seuil)) (un plan par valeur) ; index couvrant
```
**À observer** : *logical reads* de la 2ᵉ requête selon la stratégie ; **lignes estimées ≪ lignes réelles** dans le plan réel ; `OPTION (RECOMPILE)` visible dans le journal. Refaites (`DBCC FREEPROCCACHE`) dans l'**ordre inverse** (large puis étroit) pour voir le plan « scan » réutilisé à tort pour la requête étroite.
```sql
DROP INDEX IX_Lab_Planets_Population ON dbo.Planets;
```

### Démo 3.2 — Dessiner l'index (10 min)
**Avant :**
```csharp
await Mesurer("AVANT : sans index adapté", async db =>
    (await db.Inhabitants.AsNoTracking().Where(i => i.Gender == 1 && i.IsActive)
        .OrderByDescending(i => i.Credits).ThenBy(i => i.Id).Take(20).ToListAsync()).Count, afficherSql: true);       // scan + Top N Sort sur 4 M de lignes
```
**Après :** créer l'index (égalités d'abord, tri ensuite) puis **relancer le même C#** :
```sql
SET STATISTICS IO, TIME ON;
CREATE INDEX IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants (Gender, IsActive, Credits DESC, Id);
-- Seek sur (Gender=1, IsActive=1) puis lecture dans l'ordre de Credits : 20 lignes + 20 Key Lookups
```
**Variantes** : index dans le **mauvais ordre** `(Credits DESC, Gender, IsActive, Id)` ; sans `Credits` en clé ; tri sur une **autre** colonne (`BirthYear`) : le tri revient.
```sql
DROP INDEX IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants;
```

### Démo 3.3 — Pagination par clé (12 min)
**Avant :**
```csharp
await Mesurer("AVANT : Skip(5 000 000)", async db =>
    (await db.Trades.AsNoTracking().OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Skip(5_000_000).Take(50).ToListAsync()).Count, afficherSql: true);
```
**Après :**
```csharp
var (dateRef, idRef) = (new DateTime(2025, 6, 1), 4_500_000L);
await Mesurer("APRÈS : keyset", async db =>
    (await db.Trades.AsNoTracking().Where(t => t.TradedAt > dateRef || (t.TradedAt == dateRef && t.Id > idRef))
        .OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Take(50).ToListAsync()).Count, afficherSql: true);
```
```sql
CREATE INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades (TradedAt, Id);          -- relancer les deux C# avant puis après
DROP INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades;
```
**À observer** : l'offset croît avec la profondeur ; le keyset est **plat seulement avec l'index**. Limite : pas de saut direct à la page 137.

### Démo 3.4 — Top N par groupe : la cardinalité décide (10 min)
**Avant :**
```csharp
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Species.AsNoTracking().Select(s => new { s.Classification, s.Id, s.AverageLifespan }).ToListAsync();
    return l.GroupBy(x => x.Classification).SelectMany(g => g.OrderByDescending(x => x.AverageLifespan).Take(3)).Count();
});
```
**Après (1) — `APPLY` :**
```csharp
await Mesurer("APRÈS (1) : SelectMany + Take (APPLY)", async db =>
    (await db.Species.Select(s => s.Classification).Distinct()
        .SelectMany(c => db.Species.Where(s => s.Classification == c).OrderByDescending(s => s.AverageLifespan).ThenBy(s => s.Id).Take(3)).ToListAsync()).Count, afficherSql: true);
```
**Après (2) — `ROW_NUMBER` :**
```csharp
await Mesurer("APRÈS (2) : ROW_NUMBER", async db =>
    (await db.Species.FromSql($"""
        SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Classification ORDER BY AverageLifespan DESC, Id) AS rn FROM dbo.Species) x WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);
```
```sql
CREATE INDEX IX_Lab_Species_Class_Life ON dbo.Species (Classification, AverageLifespan DESC, Id);      -- relancer (1) et (2)
DROP INDEX IX_Lab_Species_Class_Life ON dbo.Species;
```
**À observer** : peu de gros groupes → `APPLY` + index gagne (un seek par classification) ; **beaucoup de petits groupes** (essayez `HomeworldId`) → `ROW_NUMBER` rattrape. Aucune méthode universelle.

### TP (6 min) — « Dessiner l'index »
Pour `db.Inhabitants.Where(i => i.PlanetId == 12 && i.IsActive).OrderBy(i => i.LastName).Take(10)` : proposez l'index idéal et prouvez avec le plan. Que se passe-t-il avec `PlanetId < 50000` ?
<details><summary>Correction</summary>

Index `(PlanetId, IsActive, LastName)`. Avec une **plage** sur `PlanetId`, les lignes lues dans l'ordre de l'index ne sont plus triées globalement sur `LastName` : le tri **réapparaît**. Tout ce qui suit une colonne en plage dans la clé ne sert plus au tri.
</details>

- **À donner en autonomie** : 7.1.x (compilation, modèle, pool), 7.2.x (prédicats dynamiques, spécifications, tris), 7.4.1, 7.4.3 à 7.4.6, 7.5.3, 7.6.x, 7.7.x.
- **Option « fort niveau »** : 7.2.1 (prédicats dynamiques avec `ExpressionVisitor`), 7.6.3 (intercepteur d'expression : garde « `Take` sans `OrderBy` »).
- **Risque** : `DBCC FREEPROCCACHE` est global — à annoncer.

---

## Bloc 4 — Millions de lignes (50 min)

| Min | Activité |
|--:|---|
| 0–10 | Flux et mémoire (démo 4.1) |
| 10–22 | Écritures de masse : `ExecuteUpdate` et ses limites (démo 4.2) |
| 22–32 | Lots, verrous et journal (démo 4.3) |
| 32–40 | Index analytique : *columnstore* (démo 4.4) |
| 40–46 | Verrous et isolation (démo 4.5) |
| 46–50 | **TP** (insertions massives en démonstration préparée : démo 4.6) |

### Démo 4.1 — Flux, suivi et mémoire (10 min)
**Avant — « streaming » avec suivi :**
```csharp
await Mesurer("AVANT : foreach + suivi", async db =>
{
    decimal capital = 0;
    await foreach (var c in db.Companies.AsAsyncEnumerable()) capital += c.Capital;
    return $"capital={capital:N0} entités suivies={db.ChangeTracker.Entries().Count():N0}";            // 1 000 000
});
```
**Après :**
```csharp
await Mesurer("APRÈS : foreach + AsNoTracking", async db =>
{
    decimal capital = 0;
    await foreach (var c in db.Companies.AsNoTracking().AsAsyncEnumerable()) capital += c.Capital;
    return $"capital={capital:N0} entités suivies={db.ChangeTracker.Entries().Count():N0}";            // 0
});
```
**Pic de mémoire** (à placer autour du code mesuré) :
```csharp
long pic = 0; using var arret = new CancellationTokenSource();
var echantillonneur = Task.Run(async () => { while (!arret.IsCancellationRequested) { pic = Math.Max(pic, GC.GetTotalMemory(false)); await Task.Delay(15); } });
// … code à mesurer …
arret.Cancel(); await echantillonneur; Console.WriteLine($"pic du tas managé : {pic / 1_048_576.0:N0} Mo");
```
**À observer** : pic de tas, `GC2` ; le « streaming » avec suivi n'en est pas un ; variante `ToListAsync` de 1 M d'entités (tout en mémoire).

### Démo 4.2 — `ExecuteUpdate` et ses limites (12 min)
> Toutes les écritures sont **annulées** (`DansTransactionAnnulee`).

**Avant — lire, modifier, enregistrer ~80 000 biens de contrebande :**
```csharp
await Mesurer("AVANT : ToList + SaveChanges", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var l = await db.Goods.Where(g => g.IsContraband).ToListAsync();
        foreach (var g in l) g.BasePrice *= 1.10m;
        var n = await db.SaveChangesAsync();
        return $"{n:N0} lignes ; suivies : {db.ChangeTracker.Entries().Count():N0}";
    }));
```
**Après — une instruction :**
```csharp
await Mesurer("APRÈS : ExecuteUpdate", async db =>
    await DansTransactionAnnulee(db, async () =>
        $"{await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.10m)):N0} lignes"), afficherSql: true);
```
**Les limites** (ce que le gain ne dit pas) :
```csharp
await Mesurer("LIMITE : entité périmée après ExecuteUpdate", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var g = await db.Goods.FirstAsync(x => x.Id == 42);
        await db.Goods.Where(x => x.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(x => x.BasePrice, x => x.BasePrice + 1000));
        var enBase = await db.Goods.AsNoTracking().Where(x => x.Id == 42).Select(x => x.BasePrice).FirstAsync();
        return $"en mémoire : {g.BasePrice} ; en base : {enBase}";                             // l'entité suivie est périmée
    }));
```
**À observer** : 80 000 `UPDATE` (en lots) contre un seul ; entités suivies ; mémoire. **Limites à citer** : suivi non synchronisé, `SaveChangesInterceptor`/audit contournés, jetons de concurrence non vérifiés, cascades côté client absentes (module 05, 5.3).
**Index** : un index sur la colonne modifiée **ralentit** l'écriture ; un index filtré aide à **trouver** les lignes :
```sql
CREATE INDEX IX_Lab_Goods_Contra ON dbo.Goods (Id) WHERE IsContraband = 1;
-- relancer « APRÈS : ExecuteUpdate » ; puis avec CREATE INDEX IX_Lab_Goods_BasePrice ON dbo.Goods (BasePrice) : comparer l'écriture
DROP INDEX IF EXISTS IX_Lab_Goods_Contra ON dbo.Goods; DROP INDEX IF EXISTS IX_Lab_Goods_BasePrice ON dbo.Goods;
```

### Démo 4.3 — Lots, verrous et journal (10 min)
**Avant — une seule mise à jour de 1 M de lignes :**
```csharp
await Mesurer("AVANT : UPDATE de 1 M de lignes", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var n = await db.Goods.Where(g => g.Id <= 1_000_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));
        var verrous = await db.Database.SqlQuery<string>($"SELECT CONCAT(resource_type, '/', request_mode) AS Value FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type IN ('OBJECT','PAGE','KEY','RID') GROUP BY resource_type, request_mode").ToListAsync();
        var journal = (await db.Database.SqlQuery<long>($"SELECT database_transaction_log_bytes_used AS Value FROM sys.dm_tran_database_transactions WHERE database_id = DB_ID() AND transaction_id = (SELECT transaction_id FROM sys.dm_tran_current_transaction)").ToListAsync()).FirstOrDefault();
        return $"{n:N0} lignes ; verrous : {string.Join(", ", verrous)} ; journal : {journal / 1_048_576.0:N0} Mo";
    }));
```
**Après — lots de 20 000 :**
```csharp
await Mesurer("APRÈS : lots de 20 000", async db =>
{
    long total = 0; long journalMax = 0;
    for (var debut = 1; debut <= 1_000_000; debut += 20_000)
        total += await DansTransactionAnnulee(db, async () =>
        {
            var n = await db.Goods.Where(g => g.Id >= debut && g.Id < debut + 20_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));
            var journal = (await db.Database.SqlQuery<long>($"SELECT database_transaction_log_bytes_used AS Value FROM sys.dm_tran_database_transactions WHERE database_id = DB_ID() AND transaction_id = (SELECT transaction_id FROM sys.dm_tran_current_transaction)").ToListAsync()).FirstOrDefault();
            journalMax = Math.Max(journalMax, journal); return n;
        });
    return $"{total:N0} lignes ; journal max par lot : {journalMax / 1_048_576.0:N1} Mo";
});
```
**À observer** : verrou **de table** (escalade, au-delà d'environ 5 000 verrous de ligne) contre verrous de page/ligne ; journal par transaction ; durée totale (un peu plus longue, mais la base reste utilisable).

### Démo 4.4 — Index analytique : *columnstore* (8 min, **créé à l'avance**)
**Avant :**
```csharp
await Mesurer("AVANT : agrégat sans columnstore", async db =>
    (await db.Trades.GroupBy(t => t.GoodId % 100).Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice), N = g.Count() }).ToListAsync()).Count, afficherSql: true);
```
**Après :** créer l'index (**préparer avant la séance** : plusieurs secondes à dizaines de secondes) puis relancer **le même C#** :
```sql
CREATE NONCLUSTERED COLUMNSTORE INDEX IX_Lab_Trades_CS ON dbo.Trades (GoodId, CompanyId, Quantity, UnitPrice, TradedAt, TradeType);
SELECT i.name, CAST(SUM(ps.used_page_count) * 8 / 1024.0 AS decimal(10,1)) AS taille_mo
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.Trades') GROUP BY i.name ORDER BY taille_mo DESC;
-- après la démo : DROP INDEX IX_Lab_Trades_CS ON dbo.Trades;
```
**À observer** : opérateur *Columnstore Index Scan* en **mode batch** ; lectures et temps très inférieurs pour les **agrégats** ; taille de l'index ; **coût d'écriture** (insérez des lignes dans une transaction annulée avec et sans l'index). Inutile pour les lectures ponctuelles.

### Démo 4.5 — Verrous et isolation (6 min)
**Avant — le lecteur est bloqué :**
```csharp
await using var ecrivain = NewDb(); await using var lecteur = NewDb();
await using var tx = await ecrivain.Database.BeginTransactionAsync();
await ecrivain.Goods.Where(g => g.Id <= 300_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));      // transaction ouverte
lecteur.Database.SetCommandTimeout(5);
var sw = Stopwatch.StartNew();
try { await lecteur.Goods.CountAsync(g => g.Id == 12_345); Console.WriteLine($"lu en {sw.ElapsedMilliseconds} ms"); }
catch (Exception e) { Console.WriteLine($"bloqué : {e.GetType().Name} après {sw.ElapsedMilliseconds} ms"); }
await tx.RollbackAsync();
```
```sql
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time FROM sys.dm_exec_requests r WHERE r.blocking_session_id <> 0;     -- dans un autre terminal, pendant le blocage
```
**Après — RCSI (option de base à remettre à `OFF` en fin de séance) :**
```sql
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
-- relancer le scénario : le lecteur n'est plus bloqué et lit la dernière valeur VALIDÉE
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;
```
**À citer** : `READ UNCOMMITTED`/`NOLOCK` = jamais bloqué mais **lectures sales** (valeurs jamais validées, lignes lues deux fois ou ratées).

### Démo 4.6 — Insertions massives (démonstration préparée, 4 min de commentaire)
```sql
CREATE TABLE dbo.Lab_Item (Id int IDENTITY(1,1) PRIMARY KEY, Nom nvarchar(30) NOT NULL, Valeur int NOT NULL);
```
```csharp
// public class LabItem { public int Id { get; set; } public string Nom { get; set; } = ""; public int Valeur { get; set; } }
// classe partielle du contexte : public DbSet<LabItem> LabItems => Set<LabItem>();  et  modelBuilder.Entity<LabItem>().ToTable("Lab_Item");

await Mesurer("AVANT : AddRange + SaveChanges (100 000)", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        db.LabItems.AddRange(Enumerable.Range(0, 100_000).Select(i => new LabItem { Nom = $"x{i}", Valeur = i }));
        return $"{await db.SaveChangesAsync():N0} lignes";
    }));

await Mesurer("APRÈS (1) : SqlBulkCopy", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var table = new DataTable(); table.Columns.Add("Nom", typeof(string)); table.Columns.Add("Valeur", typeof(int));
        for (var i = 0; i < 100_000; i++) table.Rows.Add($"x{i}", i);
        var cn = (SqlConnection)db.Database.GetDbConnection();
        var tx = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "dbo.Lab_Item", BatchSize = 10_000 };
        bulk.ColumnMappings.Add("Nom", "Nom"); bulk.ColumnMappings.Add("Valeur", "Valeur");
        await bulk.WriteToServerAsync(table);
        return "100 000 lignes";
    }));

await Mesurer("APRÈS (2) : INSERT … SELECT", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Database.ExecuteSqlAsync($"INSERT INTO dbo.Lab_Item (Nom, Valeur) SELECT CONCAT(N'x', value), value FROM GENERATE_SERIES(1, 100000)")));
```
**À observer** : temps et allocations des trois ; ajoutez deux index sur `Lab_Item` et relancez : l'écart se resserre (le coût d'écriture dans les index domine). `GENERATE_SERIES` demande SQL Server 2022 et un niveau de compatibilité 160.

### TP (4 min) — « Choisir la taille du lot »
Pour 1 M de lignes, faites varier le lot (1 000 ; 5 000 ; 20 000 ; 100 000) : type de verrou, journal max par transaction, temps total. Quelle taille évitez-vous ?
<details><summary>Correction</summary>

Autour de **5 000 lignes** SQL Server **escalade** les verrous de ligne en verrou de table : au-dessus, chaque lot bloque la table pendant sa durée. Sur une table active, restez en dessous (quelques milliers) ; sinon prenez plus gros.
</details>

- **À donner en autonomie** : 5.2, 5.4 à 5.10, 8.3, 8.6 à 8.12, 8.15.

---

## Pause (10 min)

---

## Bloc 5 — Nouveautés d'EF Core (15 min)

> Contenu **recoupé avec la documentation officielle** : EF 8 (`OPENJSON`), EF 9 (`Count != 0` → `EXISTS`), EF 10 (`LeftJoin`/`RightJoin`, filtres nommés, `ExecuteUpdate` à lambda, paramètres multiples). **EF Core 11 : non recoupé** — à compléter avant l'atelier (module 09, 9.11).

### Démo 5.1 — `Contains` : trois traductions (5 min)
```csharp
var ids = Enumerable.Range(1, 10).ToList();
Console.WriteLine(NewDb().Goods.Where(g => ids.Contains(g.Id)).ToQueryString());                              // mode par défaut de votre version
Console.WriteLine(NewDb().Goods.Where(g => EF.Constant(ids).Contains(g.Id)).ToQueryString());                 // valeurs inlinées
Console.WriteLine(NewDb().Goods.Where(g => EF.Parameter(ids).Contains(g.Id)).ToQueryString());               // un paramètre JSON + OPENJSON
Console.WriteLine(NewDb().Goods.Where(g => EF.MultipleParameters(ids).Contains(g.Id)).ToQueryString());       // un paramètre par valeur
// Globalement : .UseSqlServer(Cs, o => o.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter))   // retrouver le comportement d'EF 8-9
```
**À observer** : la forme du SQL, le nombre de plans en cache pour 4 tailles de liste, l'estimation de lignes dans le plan.

### Démo 5.2 — `LeftJoin` (3 min)
**Avant :**
```csharp
await Mesurer("AVANT : GroupJoin + DefaultIfEmpty", async db =>
    (await db.Planets.Where(p => p.Id <= 300_000)
        .GroupJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, ms) => new { p, ms })
        .SelectMany(x => x.ms.DefaultIfEmpty(), (x, m) => new { x.p.Name, Marche = m == null ? null : m.Name }).ToListAsync()).Count, afficherSql: true);
```
**Après :**
```csharp
await Mesurer("APRÈS : LeftJoin", async db =>
    (await db.Planets.Where(p => p.Id <= 300_000)
        .LeftJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, m) => new { p.Name, Marche = m == null ? null : m.Name }).ToListAsync()).Count, afficherSql: true);
```
**À observer** : SQL identique, **aucun gain de vitesse attendu** ; le gain est la lisibilité et l'absence d'inner join accidentel.

### Démo 5.3 — `ExecuteUpdate` à lambda ordinaire (4 min)
**Avant** (combinatoire de branches) :
```csharp
Task<int> Ajuster(StarWarsContext db, decimal? facteurPrix, decimal? deltaPoids)
{
    if (facteurPrix is not null && deltaPoids is not null) return db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * facteurPrix.Value).SetProperty(g => g.WeightKg, g => g.WeightKg + deltaPoids.Value));
    if (facteurPrix is not null) return db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * facteurPrix.Value));
    return Task.FromResult(0);        // …et les autres cas
}
```
**Après (EF 10) :**
```csharp
bool deltaPoidsFourni = true;
await Mesurer("APRÈS : setters conditionnels", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.10m);
            if (deltaPoidsFourni) s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1);
        })), afficherSql: true);
```

### Démo 5.4 — Filtres de requête nommés (3 min)
```csharp
// dans OnModelCreatingPartial (EF 10) :
//   modelBuilder.Entity<Market>().HasQueryFilter("PasNoir", m => !m.IsBlackMarket).HasQueryFilter("Locataire", m => m.PlanetId <= 1_000_000);
// requêtes :
//   db.Markets.CountAsync();                                       // les deux filtres
//   db.Markets.IgnoreQueryFilters(["PasNoir"]).CountAsync();       // garde « Locataire » (signature à vérifier dans votre version)
```
**Avant EF 10** : un seul filtre global ; `IgnoreQueryFilters()` les désactivait **tous**. **À observer** : `ToQueryString()` avec 0, 1, 2 filtres désactivés.

- **À citer** (sans démonstration) : précompilation/NativeAOT (expérimental en EF 9), recherche vectorielle (SQL Server 2025 uniquement), JSON via `ComplexProperty(...).ToJson()`, **EF 11 à vérifier**.
- **« Bundler de requête »** : annoncer l'interprétation retenue (migration *bundle*, batching de `SaveChanges`, ou plusieurs instructions par aller-retour ; voir 5.4 et 5.8).

---

## Bloc 6 — Sortir de LINQ, LINQ contre procédure stockée (30 min)

| Min | Activité |
|--:|---|
| 0–5 | Signaux d'alerte et arbre de décision |
| 5–15 | **Scénario S3 (top 3 par groupe)** avec cinq techniques (démo 6.1) |
| 15–21 | Chemin chaud : EF, compilé, Dapper, ADO.NET (démo 6.2) |
| 21–24 | LINQ contre procédure : tableau de décision |
| 24–30 | **TP** « comité d'architecture » |

**Signaux d'alerte** : fenêtrage/`PIVOT`/CTE récursive/`MERGE` ; traitement ensembliste lourd ; hints/tables temporaires/TVP ; SQL irréparable **malgré** le travail des blocs 1 à 4 ; chemin chaud mesuré ; export de masse ; insertion massive.

### Démo 6.1 — S3 : cinq techniques, un seul résultat (10 min)
**Objets SQL :**
```sql
CREATE OR ALTER FUNCTION dbo.Lab_Top3Echanges (@GoodMax int) RETURNS TABLE AS RETURN
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3;
GO
CREATE OR ALTER PROCEDURE dbo.Lab_Top3Proc @GoodMax int AS BEGIN SET NOCOUNT ON;
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3; END
```
**Avant :**
```csharp
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.GoodId <= 1_000).ToListAsync();
    return l.GroupBy(t => t.GoodId).SelectMany(g => g.OrderByDescending(t => t.UnitPrice).Take(3)).Count();
});
```
**Après (cinq techniques) :**
```csharp
await Mesurer("S3 EF navigation", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 1_000)
        .Select(g => g.Trades.OrderByDescending(t => t.UnitPrice).Take(3).Select(t => new { t.GoodId, t.Id, t.UnitPrice }).ToList()).ToListAsync()).Sum(x => x.Count), afficherSql: true);

await Mesurer("S3 EF FromSql ROW_NUMBER", async db =>
    (await db.Trades.FromSql($"""
        SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= 1000) x WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);

await Mesurer("S3 fonction table", async db =>
    (await db.Database.SqlQuery<TopLigne>($"SELECT GoodId, Id, UnitPrice FROM dbo.Lab_Top3Echanges({1000})").ToListAsync()).Count, afficherSql: true);

await Mesurer("S3 procédure", async db =>
    (await db.Database.SqlQuery<TopLigne>($"EXEC dbo.Lab_Top3Proc {1000}").ToListAsync()).Count, afficherSql: true);

await Mesurer("S3 Dapper", async db =>
{
    await db.Database.OpenConnectionAsync();
    var l = await db.Database.GetDbConnection().QueryAsync<TopLigne>(
        "/* lab:S3-dapper */ SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @max) x WHERE rn <= 3", new { max = 1000 });
    return l.Count();
});
```
**Index** : `CREATE INDEX IX_Lab_Trades_Good_Price ON dbo.Trades (GoodId, UnitPrice DESC) INCLUDE (MarketId, CompanyId, TradeType, Quantity, TradedAt);` — relancer ; le plan perd son tri pour **toutes** les techniques ; puis `DROP INDEX IX_Lab_Trades_Good_Price ON dbo.Trades;`.
**À observer** : **mêmes lignes** (vérifier), **même plan et mêmes lectures logiques** ; les écarts se lisent en **temps .NET** et **allocations** (Dapper < EF) ; les commandes Dapper n'apparaissent pas dans le compteur EF (utiliser le commentaire-tag et `sys.dm_exec_query_stats`).

### Démo 6.2 — Chemin chaud : 2 000 lectures par clé (6 min)
```csharp
await Mesurer("S1 EF normal", async db => { long s = 0; for (var i = 1; i <= 2000; i++) s += (await db.Goods.AsNoTracking().FirstAsync(g => g.Id == i)).Id; return s; });

await Mesurer("S1 Dapper", async _ =>
{
    await using var cn = new SqlConnection(Cs); await cn.OpenAsync(); long s = 0;
    for (var i = 1; i <= 2000; i++) s += (await cn.QuerySingleAsync<Good>("/* lab:S1-dapper */ SELECT Id, Sku, Name, Category, BasePrice, WeightKg, IsContraband, IsPerishable FROM dbo.Goods WHERE Id = @id", new { id = i })).Id;
    return s;
});

await Mesurer("S1 ADO.NET", async _ =>
{
    await using var cn = new SqlConnection(Cs); await cn.OpenAsync();
    await using var cmd = new SqlCommand("/* lab:S1-ado */ SELECT Id FROM dbo.Goods WHERE Id = @id", cn);
    var p = cmd.Parameters.Add("@id", SqlDbType.Int); long s = 0;
    for (var i = 1; i <= 2000; i++) { p.Value = i; await using var r = await cmd.ExecuteReaderAsync(); if (await r.ReadAsync()) s += r.GetInt32(0); }
    return s;
});
```
**À observer** : temps par appel et allocations ; l'écart (quelques dizaines de µs par appel) à rapporter au **gain total** (fréquence × gain) avant de quitter EF.

### Tableau de décision — LINQ contre procédure stockée
| | LINQ (EF) | `FromSql` | Vue / fonction table | Procédure |
|---|---|---|---|---|
| Composable | oui | oui (`SELECT`) | oui | **non** |
| Typage / refactoring | compilateur | partiel | partiel | texte SQL |
| Plan / performance | idem à SQL équivalent | idem | idem | idem (+ *sniffing*) |
| Droits d'accès | sur les tables | sur les tables | sur l'objet | **sur l'objet** |
| Multi-étapes, tables temporaires | non | non | non | **oui** |
| Versionnement | code | code | base | base |

**Arbre de décision** : *lente ? → mesurée ? → d'où vient le temps ?* SQL → index/projection/sargabilité/split puis `FromSql`/fonction table/vue ; transfert → projection/`AsNoTracking`/flux puis Dapper/ADO.NET ; pipeline EF mesuré → compilée/pool puis Dapper ; écriture massive → `ExecuteUpdate`/`SqlBulkCopy`/procédure ; expressivité → `FromSql`/linq2db/procédure.

### TP (6 min) — « Le comité d'architecture »
Cinq cas : export mensuel de 20 M de lignes ; classement en temps réel ; import quotidien de 500 000 lignes ; recherche par mots dans 3 M de descriptions ; API de lecture par clé à 5 000 appels/s. Pour chacun : technique, **mesure justificative**, plan B.
<details><summary>Correction</summary>

Export : flux ADO.NET/`bcp` ; classement : fenêtrage en SQL (index, vue indexée) ; import : `SqlBulkCopy` + `MERGE` ; recherche : plein texte, puis moteur dédié si facettes ; lecture par clé : EF compilé puis Dapper si le pipeline pèse. Chaque choix se justifie par **une mesure** (axe dominant, volume, fréquence) et prévoit son plan B.
</details>

- **À donner en autonomie** : S2 (export), S4 (insertion), 10.2.5 à 10.2.9.
- **Pré-requis** : paquet `Dapper` installé et testé avant l'atelier.

---

## Bloc 7 — Pièges, garde-fous, synthèse (20 min)

| Min | Activité |
|--:|---|
| 0–6 | Outils d'exploitation : compteurs EF, Query Store/DMV, `TagWith` |
| 6–14 | **Chasse au bug** : trois pièges |
| 14–18 | Garde-fous et check-list |
| 18–20 | Synthèse, nettoyage |

### Outils d'exploitation (6 min)
```bash
dotnet-counters ps
dotnet counters monitor --counters Microsoft.EntityFrameworkCore -p <PID>        # requêtes/s, taux de succès du cache de requêtes, contextes actifs, SaveChanges
```
```csharp
await db.Starships.TagWith("API GET /vaisseaux?societe={id}").Where(s => s.CompanyId == 5).ToListAsync();      // le tag apparaît en commentaire dans le SQL
```
```sql
SELECT TOP 10 SUBSTRING(st.text, 1, 120) AS debut, qs.execution_count, qs.total_logical_reads / qs.execution_count AS reads_moy, qs.total_worker_time / qs.execution_count / 1000.0 AS cpu_ms_moy
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE '%-- API GET%' ORDER BY qs.total_worker_time DESC;
```
**Query Store** (historique persistant, régressions de plan) : `ALTER DATABASE StarWarsTrade SET QUERY_STORE = ON;` puis « Top Resource Consuming Queries » / « Regressed Queries » ; **à remettre à `OFF`** en fin de séance.

### Chasse au bug — trois pièges (8 min)
**Piège 1 — `ToList()` prématuré** :
```csharp
// ❌
var tous = await db.Starships.AsNoTracking().ToListAsync();
var l = tous.Where(s => s.CompanyId == 12 && s.IsOperational).OrderByDescending(s => s.CargoCapacity).Take(5).ToList();
// ✔
var l2 = await db.Starships.AsNoTracking().Where(s => s.CompanyId == 12 && s.IsOperational).OrderByDescending(s => s.CargoCapacity).Take(5).ToListAsync();
```
**Piège 2 — `.Result` sur du code asynchrone** (famine de threads sous charge) :
```csharp
int CompterBloquant() => NewDb().Goods.CountAsync().Result;
async Task<int> CompterAsync() { await using var d = NewDb(); return await d.Goods.CountAsync(); }

ThreadPool.SetMinThreads(4, 4);                                                      // simule un pool contraint
var sw = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => CompterBloquant())));
Console.WriteLine($"bloquant : {sw.ElapsedMilliseconds} ms");
sw.Restart();
await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => CompterAsync()));
Console.WriteLine($"asynchrone : {sw.ElapsedMilliseconds} ms");
```
**Piège 3 — `First` sans tri : le résultat dépend du plan** :
```csharp
var a = await NewDb().Inhabitants.Where(i => i.Gender == 1).FirstAsync();                              // quel habitant ? relever l'Id…
// … créer IX_Lab_Inhabitants_Gender_Credits (démo 3.2), relancer : l'Id peut changer
var b = await NewDb().Inhabitants.Where(i => i.Gender == 1).OrderBy(i => i.Id).FirstAsync();           // ✔ ordre explicite
```

### Garde-fous et check-list (4 min)
```csharp
// Budget de requêtes (démo 2.2) ; garde de forme SQL :
static void ContientWhere(IQueryable q) { if (!q.ToQueryString().Contains("WHERE")) throw new Exception("pas de filtre SQL"); }
```
**Check-list de revue de code** :
- [ ] Aucun `ToList()`/`AsEnumerable()` avant `Where`/`OrderBy`/`Count`/`Take`.
- [ ] Aucun `Func<>` ni `IEnumerable<T>` dans les signatures d'accès aux données.
- [ ] Tout `Take`/`Skip` précédé d'un `OrderBy` **déterministe**.
- [ ] Existence = `Any()`, pas `Count() > 0`.
- [ ] Pas de requête dans une boucle ; plusieurs `Include` de collections ⇒ split ou projection.
- [ ] Agrégats testés sur ensemble vide ; valeurs capturées (pas de constantes variables).
- [ ] Lectures en `AsNoTracking()` ; pas de `.Result`/`.Wait()` ; un `DbContext` par tâche concurrente.
- [ ] Filtres globaux inventoriés ; `IgnoreQueryFilters()` explicite quand nécessaire.

### Synthèse — les cinq réflexes (2 min)
1. **Mesurer avant d'optimiser** (SQL → mémoire → CPU ; `logical reads` d'abord).
2. **Ne pas déplacer de données inutiles** (projection, filtre, agrégat côté serveur).
3. **Écrire des prédicats sargables et les indexer** (et mesurer le coût d'écriture).
4. **Rester composable** (`IQueryable`, `Expression`, pas de matérialisation prématurée).
5. **Quitter LINQ par la mesure**, vers l'outil le plus étroit qui suffit.

- **À donner en autonomie** : modules 11 et 12 en entier ; la FAQ [15](15-faq.md).

---

## Priorités si le temps manque

| Priorité | Contenu | Si retard |
|---|---|---|
| **P1 — jamais supprimé** | boîte à outils, 0.1, projection (1.1), sargabilité + index (1.4), explosion cartésienne (2.1), flux/mémoire (4.1), lots et verrous (4.3), LINQ contre procédure (6.1, tableau) | maintenir |
| **P2 — réduire** | *sniffing* (3.1), keyset (3.3), top N (3.4), `ExecuteUpdate` (4.2), suivi (2.3), columnstore (4.4) | présenter **sans TP** |
| **P3 — autonomie** | le reste : démos 5.x (sauf 5.1), 6.2, 4.6, modules 11 et 12 | retirer |

## Si le temps est abondant
- 7.2.1 et 7.6.3 (arbres d'expression) en démonstration ; 8.10 (*spills*) et 8.6 (point de bascule seek/scan) avec lecture de plans ; laboratoire multi-versions EF 8 / EF 11 (9.1, 9.6) ; CTE récursive sur les factions (10.1.1).

## Supports à préparer
- Une **fiche d'une page** par bloc : objectif, commande, résultat attendu (rempli **après** la répétition).
- Un **tableau récapitulatif** à remplir : technique / temps C# / allocations / requêtes / lignes / logical reads / seek ou scan.
- La **check-list de revue** imprimée ; vos **mesures de référence** pour comparer.

## Questions encore ouvertes
1. **« Bundler de requête »** : quelle signification retenir (5.8, 9.12) ?
2. **EF Core 11** : que montrer, sachant que ses nouveautés n'ont pas été recoupées (9.11) ?
3. **SQL Server 2025** : instance disponible ou lecture seule ?
4. **Répétition générale** : qui exécute les extraits et consigne les durées ?
5. **Autre moteur** (PostgreSQL…) : à comparer ou hors périmètre ?

## Script de nettoyage (fin de séance)
```sql
DROP INDEX IF EXISTS IX_Lab_Starships_Cover          ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Starships_Cargo          ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt          ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt_Id       ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Qty_Type          ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Market_Date       ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Good_Price        ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_CS                ON dbo.Trades;      -- columnstore
DROP INDEX IF EXISTS IX_Lab_Planets_Population       ON dbo.Planets;
DROP INDEX IF EXISTS IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants;
DROP INDEX IF EXISTS IX_Lab_Species_Class_Life       ON dbo.Species;
DROP INDEX IF EXISTS IX_Lab_Goods_Contra             ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Goods_BasePrice          ON dbo.Goods;
DROP FUNCTION  IF EXISTS dbo.Lab_Top3Echanges;
DROP PROCEDURE IF EXISTS dbo.Lab_Top3Proc;
DROP TABLE     IF EXISTS dbo.Lab_Item;

-- Options de base modifiées pendant la séance : doivent être OFF
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;
ALTER DATABASE StarWarsTrade SET QUERY_STORE = OFF;

-- Vérification : ne doit rien retourner (les deux dernières lignes doivent donner 0 / OFF)
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = 'StarWarsTrade';
```
