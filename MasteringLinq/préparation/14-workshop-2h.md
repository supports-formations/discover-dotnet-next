# 14 — Workshop de 2 heures (version condensée, autonome)

> Version **courte** du workshop de 5 h ([13](13-deroule.md)), pour un public expérimenté : **intensif, mais pas trop** — 13 démonstrations **mesurées**, **trois mini-TP**.
> **Ce fichier est autonome** : chaque démonstration contient directement le code **« avant »** (sans l'amélioration) et **« après »** (avec), le SQL des index éventuels et ce qu'il faut observer. Les renvois (« module 06, 6.1 ») ne servent qu'à **approfondir**.
> Hypothèse sur le format : **2 heures au total, pause de 10 minutes incluse au bout d'une heure**, soit **110 minutes de contenu**. Pour 2 heures **hors pause**, voir « Marges et variantes ».
> **Rappel** : les extraits ont été écrits sans être exécutés ; **répétez toute la séance** avant de la présenter (durées, résultats, erreurs de compilation éventuelles).
> Une **FAQ de 50 questions** accompagne la séance : [15-faq.md](15-faq.md).

## Objectifs

À la fin des deux heures, chaque participant sait :
1. **Mesurer** une requête (SQL → mémoire → CPU) avec `ToQueryString`, `STATISTICS IO` et un plan réel.
2. Reconnaître et corriger les **cinq défauts les plus coûteux** : filtre en mémoire (`Func`/`AsEnumerable`), projection manquante, prédicat non sargable, `Include` multiples / N+1, parcours non borné.
3. Choisir une **technique avancée** adaptée (keyset, split/projection, index couvrant, lots).
4. Décider, **par la mesure**, **quand sortir de LINQ** — et vers quoi.

## Vue d'ensemble

| Horaire | Durée | Bloc |
|---|--:|---|
| 0:00 – 0:08 | 8 min | 1. Cadre et outils de mesure |
| 0:08 – 0:25 | 17 min | 2. Fondamentaux : qui exécute quoi ? (démos 1 à 3) |
| 0:25 – 0:45 | 20 min | 3. Fondamentaux : volume, projection, existence (démos 4 à 6) |
| 0:45 – 1:00 | 15 min | 4. Sargabilité, index et lecture de plan (démo 7) |
| **1:00 – 1:10** | **10 min** | **Pause** |
| 1:10 – 1:22 | 12 min | 5. Données liées : `Include`, N+1 (démos 8, 9) |
| 1:22 – 1:34 | 12 min | 6. Techniques avancées (démo 10, aperçu *sniffing*) |
| 1:34 – 1:46 | 12 min | 7. Millions de lignes (démos 11, 12) |
| 1:46 – 2:00 | 14 min | 8. Sortir de LINQ ? Décision par la mesure + synthèse (démo 13) |

**Rythme de chaque démonstration** : *prédiction* (30 s : « combien de requêtes ? de lignes ? seek ou scan ? ») → exécution de **« avant »** → exécution de **« après »** → comparaison. Les participants notent leurs mesures dans le **tableau à remplir** (fin de document).

## Préparation (avant la séance)

- [ ] Conteneur SQL Server démarré, base chargée, cache réchauffé (une lecture de `Trades`, `Goods`, `Inhabitants`).
- [ ] Le **squelette** ci-dessous compile chez chaque participant (paquets du projet : EF Core SqlServer ; `Dapper` pour la démo 13).
- [ ] Index de démonstration **créés à la demande** (on mesure « sans » puis « avec ») ; le **script de nettoyage** en fin de fichier est prêt.
- [ ] Un **jeu de résultats de secours** (vos propres mesures) en cas d'échec technique.
- [ ] Règle de sécurité rappelée : on ne modifie jamais les tables d'origine ; les écritures se font **dans des transactions annulées** ; les objets de démonstration portent `IX_Lab_` / `Lab_`.

---

## Squelette commun (à coller dans `Program.cs`)

> Les instructions de haut niveau d'abord, les déclarations de types **en bas du fichier**. Les démonstrations qui suivent supposent ces fonctions.

```csharp
using System.Data.Common;
using System.Diagnostics;
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
    var b = new DbContextOptionsBuilder<StarWarsContext>().UseSqlServer(Cs, o => o.CommandTimeout(300)).AddInterceptors(compteur);
    if (afficherSql)
        b.LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).EnableSensitiveDataLogging();
    return new StarWarsContext(b.Options);
}

// Temps, allocations, GC, nombre de requêtes SQL et de lignes lues
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

// Idem + volume réellement reçu du serveur (Mo)
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

// Écriture mesurée puis ANNULÉE (démo 12)
async Task<T> DansTransactionAnnulee<T>(StarWarsContext db, Func<Task<T>> action)
{
    await using var tx = await db.Database.BeginTransactionAsync();
    try { return await action(); } finally { await tx.RollbackAsync(); }
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
```
**Côté SQL** (outil SQL ou `docker exec -it masteringlinq-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'StarWars_Pwd123!' -d StarWarsTrade`) : `SET STATISTICS IO, TIME ON;` pour lire `logical reads` ; plan réel (Ctrl+M dans SSMS) ou `SET STATISTICS XML ON;`.

---

## Bloc 1 — Cadre et outils de mesure (8 min)

| Min | Activité |
|--:|---|
| 0–2 | Objectifs, règles de sécurité, mode de travail |
| 2–8 | Les **trois outils** : `Mesurer` (temps, allocations, requêtes, lignes), `ToQueryString`/journal, `SET STATISTICS IO, TIME ON` + plan |

**Exemple d'échauffement** (chacun l'exécute) :

```csharp
await Mesurer("échauffement : Count des biens", db => db.Goods.CountAsync(), afficherSql: true);
Console.WriteLine(NewDb().Goods.Where(g => g.IsContraband).ToQueryString());     // le SQL, sans l'envoyer
```
- **Point de contrôle** : chacun a vu **une** ligne `Mesurer` et **un** `SELECT` dans le journal.
- **Message** : *« On ne devine pas. On lit le SQL et on compte les lectures. »* (`logical reads` = l'indicateur stable.)

---

## Bloc 2 — Fondamentaux : qui exécute quoi ? (17 min)

### Démo 1 — `Func` contre `Expression` (6 min)

**Prédiction** : *« Où le filtre s'exécute-t-il ? Combien de lignes sont lues ? »*

**Avant — le filtre est un délégué :**
```csharp
await Mesurer("AVANT : Func", db =>
{
    Func<Good, bool> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return Task.FromResult(db.Goods.AsNoTracking().Where(contrebande).ToList().Count);        // Enumerable.Where : tout est lu, puis filtré en C#
}, afficherSql: true);
```
**Après — le filtre est un arbre d'expression :**
```csharp
await Mesurer("APRÈS : Expression", async db =>
{
    System.Linq.Expressions.Expression<Func<Good, bool>> contrebande = g => g.IsContraband && g.BasePrice > 99_000;
    return (await db.Goods.AsNoTracking().Where(contrebande).ToListAsync()).Count;               // Queryable.Where : WHERE côté SQL
}, afficherSql: true);
```
**À observer**
- Avant : SQL **sans `WHERE`**, ~1 M de lignes lues, allocations en centaines de Mo. Après : une poignée de lignes.
- **Le piège dans la vraie vie** : la signature d'un dépôt.

```csharp
// ❌ AVANT
public List<Good> Where(Func<Good, bool> p) => db.Goods.AsNoTracking().Where(p).ToList();
// ✔ APRÈS
public List<Good> Where(Expression<Func<Good, bool>> p) => db.Goods.AsNoTracking().Where(p).ToList();
// l'appelant écrit la même chose dans les deux cas : repo.Where(g => g.IsPerishable && g.Category == 3)
```
Le même lambda est compilé en délégué ou en arbre selon le **type du paramètre**.

### Démo 2 — Exécution différée et double énumération (5 min)

**Prédiction** : *« Combien de requêtes SQL pour `Any` + `Count` + `Max` + `ToList` sur la même variable ? »*

**Avant :**
```csharp
await Mesurer("AVANT : 4 énumérations", async db =>
{
    var q = db.Planets.AsNoTracking().Where(p => p.HasSpaceport && p.Population > 99_000_000_000);
    var any = await q.AnyAsync(); var n = await q.CountAsync();
    var max = await q.MaxAsync(p => p.Population); var l = await q.ToListAsync();
    return $"any={any} n={n} max={max} liste={l.Count}";
});

await Mesurer("AVANT : for (i < q.Count())", async db =>
{
    var q = db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30);
    decimal somme = 0;
    for (var i = 0; i < await q.CountAsync(); i++) somme += (await q.Skip(i).FirstAsync()).BasePrice;     // Count + First à CHAQUE tour
    return somme;
});
```
**Après :**
```csharp
await Mesurer("APRÈS : 1 exécution", async db =>
{
    var l = await db.Planets.AsNoTracking().Where(p => p.HasSpaceport && p.Population > 99_000_000_000).ToListAsync();
    return $"any={l.Count > 0} n={l.Count} max={l.Max(p => p.Population)} liste={l.Count}";
});

await Mesurer("APRÈS : boucle sur la liste", async db =>
{
    var l = await db.Goods.AsNoTracking().Where(g => g.IsContraband).OrderBy(g => g.Id).Take(30).ToListAsync();
    decimal somme = 0; for (var i = 0; i < l.Count; i++) somme += l[i].BasePrice;
    return somme;
});
```
**À observer** : « SQL : N req » (4 → 1 ; ≈ 61 → 1). **Nuance** : « matérialiser une fois » ne dispense pas de se demander **où** calculer : pour un agrégat, un `GroupBy` SQL est meilleur que de ramener les lignes.

### Démo 3 — Évaluation client (4 min)

**Avant — `AsEnumerable()` dès le début :**
```csharp
await Mesurer("AVANT : AsEnumerable() trop tôt", db =>
    Task.FromResult(db.Companies.AsNoTracking().AsEnumerable()
        .Count(c => c.Reputation > 95 && c.Name.Split(' ')[0].Length > 8)));
```
**Après — le filtre traduisible part en SQL, le reste (non traduisible) en C# sur un jeu réduit :**
```csharp
await Mesurer("APRÈS : SQL puis client", async db =>
    (await db.Companies.AsNoTracking().Where(c => c.Reputation > 95).Select(c => c.Name).ToListAsync())
        .Count(nom => nom.Split(' ')[0].Length > 8));
```
**À observer** : 1 M de lignes lues contre quelques dizaines de milliers ; même résultat. **Règle** : l'évaluation client est acceptable **après** que SQL a réduit l'ensemble, et sur les seules colonnes utiles.

### Mini-TP 1 (2 min, en autonomie) — « Trouver le `Func` caché »

Voici un dépôt. Pourquoi charge-t-il toute la table ?
```csharp
public class GoodRepository(StarWarsContext db)
{
    public IEnumerable<Good> GetAll() => db.Goods.AsNoTracking();
    public List<Good> Where(Func<Good, bool> p) => db.Goods.AsNoTracking().Where(p).ToList();
}
```
<details><summary>Correction</summary>

```csharp
public class GoodRepository(StarWarsContext db)
{
    public IQueryable<Good> GetAll() => db.Goods.AsNoTracking();                                     // reste composable
    public List<Good> Where(System.Linq.Expressions.Expression<Func<Good, bool>> p)                   // Expression, pas Func
        => db.Goods.AsNoTracking().Where(p).ToList();
}
```
`Func` et `IEnumerable` font appeler `Enumerable.Where` ; l'appelant n'a rien à changer (`repo.Where(g => …)`). Le compilateur ne signale rien.
</details>

---

## Bloc 3 — Fondamentaux : volume, projection, existence (20 min)

### Démo 4 — Projection et suivi (8 min)

**Prédiction** : *« Combien de colonnes lues pour afficher deux champs ? Combien d'entités suivies ? »*

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
    return $"suivies : {db.ChangeTracker.Entries().Count():N0}";                      // 200 000 !
});
```
**À observer** : colonnes du `SELECT` (8 → 2) ; Mo reçus ; entités suivies (≈ 40 000 → 0).
**Question de relance (public avancé)** : *« Et le plan SQL ? »* → inchangé sans index ; avec un index **couvrant**, le *Key Lookup* disparaît :
```sql
SET STATISTICS IO, TIME ON;
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;       -- Seek + Key Lookup
CREATE INDEX IX_Lab_Starships_Cover ON dbo.Starships (CompanyId) INCLUDE (IsOperational, Name, Model);
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;       -- Seek seul
DROP INDEX IX_Lab_Starships_Cover ON dbo.Starships;
```
L'entité complète ne profite pas de cet index (il lui faudrait les 8 colonnes) : **la projection est la condition de l'index couvrant**.

### Démo 5 — `Any` contre `Count() > 0` (4 min)

**Calibrage** (une fois) : choisissez un seuil qui donne **beaucoup** de résultats.
```csharp
Console.WriteLine(await NewDb().Starships.MinAsync(s => s.CargoCapacity) + " .. " + await NewDb().Starships.MaxAsync(s => s.CargoCapacity));
```
**Avant :**
```csharp
await Mesurer("AVANT : Count() > 0", async db => await db.Starships.CountAsync(s => s.CargoCapacity > 100) > 0, afficherSql: true);
```
**Après :**
```csharp
await Mesurer("APRÈS : Any", db => db.Starships.AnyAsync(s => s.CargoCapacity > 100), afficherSql: true);
// le cas défavorable de Any : aucun résultat (seuil au-delà du maximum)
await Mesurer("Any sans résultat", db => db.Starships.AnyAsync(s => s.CargoCapacity > 999_999_999), afficherSql: true);
```
**À observer** : `COUNT(*)` contre `EXISTS` ; *logical reads* (le `Any` s'arrête dès la première ligne) ; le **cas sans résultat** : les deux parcourent tout… sauf avec un index :
```sql
CREATE INDEX IX_Lab_Starships_Cargo ON dbo.Starships (CargoCapacity);   -- puis relancer « Any sans résultat » : Seek immédiat
DROP INDEX IX_Lab_Starships_Cargo ON dbo.Starships;
```

### Démo 6 — Tri et pagination bornée (5 min)

**Avant — aucune borne, tri non déterministe, offset profond :**
```csharp
await Mesurer("AVANT : Take sans OrderBy", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).Take(50).ToListAsync()).Count, afficherSql: true);   // avertissement EF : non déterministe

await Mesurer("AVANT : Skip(3 000 000)", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Skip(3_000_000).Take(50).ToListAsync()).Count, afficherSql: true);
```
**Après — tri déterministe (clé unique en dernier critère), borne, et position connue :**
```csharp
await Mesurer("APRÈS : tri déterministe + borne", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).Take(50).ToListAsync()).Count, afficherSql: true);

await Mesurer("APRÈS : keyset (Id > 3 000 000)", async db =>
    (await db.Inhabitants.AsNoTracking().Where(i => i.Id > 3_000_000).OrderBy(i => i.Id).Take(50).ToListAsync()).Count, afficherSql: true);
```
**À observer** : le coût de l'offset (lignes lues et ignorées) ; l'avertissement `RowLimitingOperationWithoutOrderByWarning`. (Le détail du keyset est en démo 10.)

### Mini-TP 2 (3 min) — « Un écran de liste »

Corrigez ce code (liste de vaisseaux, affichage « Nom (Modèle) ») :
```csharp
var tous = await db.Starships.ToListAsync();                                                   // entités complètes, suivies
var page = tous.Where(s => s.IsOperational).OrderBy(s => s.CargoCapacity).Take(50)            // filtre/tri en mémoire, tri non déterministe
               .Select(s => $"{s.Name} ({s.Model})").ToList();
```
<details><summary>Correction</summary>

```csharp
var page = (await db.Starships.AsNoTracking()
    .Where(s => s.IsOperational)
    .OrderBy(s => s.CargoCapacity).ThenBy(s => s.Id)                 // clé unique en dernier critère
    .Take(50)                                                        // borne
    .Select(s => new { s.Name, s.Model }).ToListAsync())             // colonnes utiles seulement
    .Select(s => $"{s.Name} ({s.Model})").ToList();
```
Projection, `Where` et `OrderBy` côté serveur, `Take` plafonné, pas de suivi.
</details>

---

## Bloc 4 — Sargabilité, index et lecture de plan (15 min)

### Démo 7 — Prédicat sargable + index : le couple indissociable (10 min)

**Prédiction** : *« `Sku.ToLower() == x` sur une colonne indexée : seek ou scan ? »*

**Avant — une fonction sur la colonne :**
```csharp
await Mesurer("AVANT : Sku.ToLower() ×20", async db =>
{
    var trouves = 0;
    for (var i = 1; i <= 20; i++) trouves += await db.Goods.CountAsync(g => g.Sku.ToLower() == $"gd-{i * 37_000:D10}");       // LOWER([Sku]) = …
    return trouves;
});
await Mesurer("AVANT : Year/Month sur la colonne", db => db.Trades.CountAsync(t => t.TradedAt.Year == 2025 && t.TradedAt.Month == 3), afficherSql: true);
```
**Après — le calcul passe du côté de la constante :**
```csharp
await Mesurer("APRÈS : Sku == x ×20", async db =>
{
    var trouves = 0;
    for (var i = 1; i <= 20; i++) trouves += await db.Goods.CountAsync(g => g.Sku == $"gd-{i * 37_000:D10}");                  // collation CI_AS : déjà insensible à la casse
    return trouves;
});
var debut = new DateTime(2025, 3, 1); var fin = debut.AddMonths(1);
await Mesurer("APRÈS : intervalle de dates", db => db.Trades.CountAsync(t => t.TradedAt >= debut && t.TradedAt < fin), afficherSql: true);
```
**Sans puis avec index** (`Goods.Sku` en a déjà un ; `Trades.TradedAt` non) :
```sql
SET STATISTICS IO, TIME ON;
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                          -- scan
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                     -- scan aussi (pas d'index)
GO
CREATE INDEX IX_Lab_Trades_TradedAt ON dbo.Trades (TradedAt);
GO
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                          -- Index Scan (tout l'index)
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                     -- Index Seek (une plage)
GO
DROP INDEX IX_Lab_Trades_TradedAt ON dbo.Trades;
```
Relancez ensuite le C# « intervalle de dates » **avant puis après** l'index.

**À observer** (`STATISTICS IO` + plan) :
1. **Sans index**, les deux écritures scannent ;
2. **Avec index**, seule l'écriture **sargable** fait un *Seek* ;
3. les *logical reads* chutent de plusieurs ordres de grandeur ;
4. sur `Sku`, le `LOWER(...)` ignore l'index pourtant présent (vérifiez le plan : un paramètre `nvarchar` sur une colonne `varchar` peut aussi jouer — module 02, 2.10).

**Message** : *« Il faut les deux : un prédicat sargable ET un index. »*

### Lecture de plan (5 min)

Sur le plan **réel** de la requête « intervalle de dates » (avec l'index) : opérateur d'accès (*Seek*), **lignes estimées / réelles**, absence de *Key Lookup*, avertissements éventuels. Comparez avec la version `YEAR(...)` (*Index Scan*).

### Mini-TP 3 (à la maison) — « Corriger cinq prédicats »

Réécrivez de façon sargable : `t.TradedAt.Year == 2024` ; `i.BirthYear + 30 > 2000` ; `g.Sku.Substring(0, 3) == "GD-"` ; `g.Name.ToUpper() == "KYBER DURASTEEL"` ; `s.CargoCapacity / 10 > 500`.
<details><summary>Correction</summary>

`t.TradedAt >= new DateTime(2024,1,1) && t.TradedAt < new DateTime(2025,1,1)` ; `i.BirthYear > 1970` ; `g.Sku.StartsWith("GD-")` ; `g.Name == "kyber durasteel"` (collation insensible) ; `s.CargoCapacity > 5_000` (vérifier l'équivalence de la division entière avec le SQL). `ToQueryString()` avant/après pour chacune.
</details>

**Point de contrôle fin de partie 1** : chacun sait **prédire** et **vérifier** : nombre de requêtes, lignes lues, seek/scan, entités suivies.

---

## Pause (10 min) · 1:00 – 1:10

---

## Bloc 5 — Données liées : `Include` et N+1 (12 min)

### Démo 8 — Explosion cartésienne (7 min)

**Prédiction** : *« Un bien a ~6 échanges, ~4 `MarketGoods`, ~3 cargos : combien de lignes pour `Include` des trois ? »* (≈ 6 × 4 × 3 = 72 par bien.)

**Avant — trois collections en une seule requête :**
```csharp
await MesurerReseau("AVANT : single query, 3 collections", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSingleQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);
```
**Après (1) — une requête par collection :**
```csharp
await MesurerReseau("APRÈS (1) : AsSplitQuery", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSplitQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);
```
**Après (2) — seulement ce qu'on affiche :**
```csharp
await MesurerReseau("APRÈS (2) : projection", async db =>
    (await db.Goods.Where(g => g.Id <= 2_000)
        .Select(g => new { g.Sku, Echanges = g.Trades.Count(), Marches = g.MarketGoods.Count(), Cargos = g.StarshipCargos.Count() })
        .ToListAsync()).Count);
```
**À observer** : lignes lues **produit** (≈ 144 000) contre **somme** (≈ 26 000) contre 2 000 ; Mo reçus ; l'avertissement d'EF sur les collections multiples ; 1 requête → 4 en split. **Contrepartie du split** : requêtes **non atomiques** (module 03, 3.9).

### Démo 9 — N+1 et budget de requêtes (5 min)

**Avant :**
```csharp
await Mesurer("AVANT : N+1", async db =>
{
    var societes = await db.Companies.AsNoTracking().Where(c => c.Id <= 1_000).ToListAsync();
    var total = 0;
    foreach (var c in societes) total += await db.Trades.CountAsync(t => t.CompanyId == c.Id);          // 1 requête par société
    return total;
});
```
**Après :**
```csharp
await Mesurer("APRÈS : GroupBy SQL", async db =>
    (await db.Trades.Where(t => t.CompanyId <= 1_000).GroupBy(t => t.CompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Sum(x => x.N));
```
**Le garde-fou** (à mettre dans les tests d'intégration) :
```csharp
async Task<T> AvecMaxRequetes<T>(int max, Func<Task<T>> action)
{
    compteur.Reset(); var r = await action();
    if (compteur.Requetes > max) throw new InvalidOperationException($"{compteur.Requetes} requêtes (max {max}) : N+1 probable");
    return r;
}
// await AvecMaxRequetes(2, () => MaMethodeDeService());   // échoue dès qu'une boucle revient
```
**À observer** : 1 001 requêtes → 1. **Message** : un N+1 se **détecte par un compteur**, pas à l'œil. *(En autonomie : 3.5 lazy loading, 3.9 cohérence du split.)*

---

## Bloc 6 — Techniques avancées (12 min)

### Démo 10 — Pagination par clé (6 min)

**Prédiction** : *« La page 100 000 de 50 lignes coûte-t-elle autant que la page 1 ? »*

**Avant :**
```csharp
await Mesurer("AVANT : Skip(5 000 000)", async db =>
    (await db.Trades.AsNoTracking().OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Skip(5_000_000).Take(50).ToListAsync()).Count, afficherSql: true);
```
**Après :**
```csharp
var (dateRef, idRef) = (new DateTime(2025, 6, 1), 4_500_000L);          // la dernière position de la page précédente
await Mesurer("APRÈS : keyset", async db =>
    (await db.Trades.AsNoTracking()
        .Where(t => t.TradedAt > dateRef || (t.TradedAt == dateRef && t.Id > idRef))
        .OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Take(50).ToListAsync()).Count, afficherSql: true);
```
**Sans puis avec index** (relancez les deux C# avant et après) :
```sql
SET STATISTICS IO, TIME ON;
CREATE INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades (TradedAt, Id);
-- offset : lit 5 000 050 entrées d'index ; keyset : Seek + 50 lignes
DROP INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades;
```
**À observer** : l'offset croît avec la profondeur ; le keyset est **plat** — **seulement avec l'index** (sans lui, il scanne aussi). Limite : pas de saut direct à la page 137.

### Aperçu — Le même code, deux plans : *parameter sniffing* (6 min, formateur seulement)

Index « sans INCLUDE » (lookups) puis requête exécutée avec un seuil **étroit** puis **large** :
```sql
CREATE INDEX IX_Lab_Planets_Population ON dbo.Planets (Population);
DBCC FREEPROCCACHE;                                                  -- conteneur de test uniquement
```
```csharp
long etroit = 99_000_000_000, large = 1_000_000_000;                 // calibrez avec COUNT : ~1 000 lignes contre plusieurs centaines de milliers
IQueryable<Planet> Q(StarWarsContext db, long seuil) => db.Planets.AsNoTracking().Where(p => p.Population > seuil);

await Mesurer("étroit puis large (même plan réutilisé)", async db =>
{
    var a = (await Q(db, etroit).ToListAsync()).Count;               // le plan « seek + lookups » est compilé pour cette valeur…
    var b = (await Q(db, large).ToListAsync()).Count;                // …et réutilisé pour une valeur large
    return $"{a} + {b}";
});
```
**Remèdes** (à citer, sans les coder) : constante `EF.Constant(seuil)` (un plan par valeur), `OPTION (RECOMPILE)` via un intercepteur de commandes (module 07, 7.5.1), index couvrant (`INCLUDE`) qui rend le plan unique bon pour tous.
**À observer** : dans le plan réel de la 2ᵉ requête, **lignes estimées ≪ lignes réelles** ; lectures disproportionnées. **Message** : *« Si le plan est mauvais, regardez d'abord estimé contre réel. »*
```sql
DROP INDEX IX_Lab_Planets_Population ON dbo.Planets;
```

---

## Bloc 7 — Millions de lignes (12 min)

### Démo 11 — Flux, suivi et mémoire (6 min)

**Prédiction** : *« Un `foreach` asynchrone sur 1 M de sociétés garde-t-il la mémoire basse ? »*

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
// Variante à mentionner : ToListAsync() de 1 M d'entités → tout en mémoire avant de commencer
```
**Mesurer le pic de mémoire** (à placer autour du code mesuré) :
```csharp
long pic = 0; using var arret = new CancellationTokenSource();
var echantillonneur = Task.Run(async () => { while (!arret.IsCancellationRequested) { pic = Math.Max(pic, GC.GetTotalMemory(false)); await Task.Delay(15); } });
// … code à mesurer …
arret.Cancel(); await echantillonneur; Console.WriteLine($"pic du tas managé : {pic / 1_048_576.0:N0} Mo");
```
**À observer** : « Mo alloués » et **pic de tas** ; `GC2` ; le « streaming » avec suivi n'en est pas un (le pic est de l'ordre de plusieurs centaines de Mo) ; `AsNoTracking` ramène le pic à quelques dizaines de Mo.

### Démo 12 — Écritures de masse : lots, verrous, journal (6 min)

> **Toutes les écritures sont annulées** (`DansTransactionAnnulee`). Aucune donnée n'est modifiée durablement.

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
**Après — lots de 20 000 lignes (une transaction par lot) :**
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
**À observer** : type de verrou (`OBJECT`/exclusif = **escalade vers un verrou de table** pour la version en un lot, contre `PAGE`/`KEY` par lot) ; **journal par transaction** ; durée totale (un peu plus longue en lots, mais la base reste **utilisable**).
**Message** : *« `ExecuteUpdate` supprime l'objet .NET, pas le coût SQL (journal, verrous, index). »*
*(En autonomie : 8.5 columnstore, 8.13–8.14 verrous et isolation, 5.5 insertion massive.)*

---

## Bloc 8 — Sortir de LINQ ? Décision par la mesure + synthèse (14 min)

### Démo 13 — Top 3 par groupe, cinq ou six techniques, un seul résultat (9 min)

**Prédiction** : *« Laquelle sera la plus rapide ? »* (vote à main levée avant de mesurer.)

**Objets SQL** (créés pour la démo) :
```sql
CREATE OR ALTER FUNCTION dbo.Lab_Top3Echanges (@GoodMax int) RETURNS TABLE AS RETURN
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3;
GO
CREATE OR ALTER PROCEDURE dbo.Lab_Top3Proc @GoodMax int AS BEGIN SET NOCOUNT ON;
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3; END
```
**Type de résultat** (en bas de `Program.cs`) : `public record TopLigne(int GoodId, long Id, decimal UnitPrice);`

**Avant — tout rapatrier puis grouper :**
```csharp
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.GoodId <= 1_000).ToListAsync();
    return l.GroupBy(t => t.GoodId).SelectMany(g => g.OrderByDescending(t => t.UnitPrice).Take(3)).Count();
});
```
**Après — quatre à six techniques, même résultat :**
```csharp
// (1) EF : sous-requête corrélée sur la navigation
await Mesurer("S3 EF navigation", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 1_000)
        .Select(g => g.Trades.OrderByDescending(t => t.UnitPrice).Take(3).Select(t => new { t.GoodId, t.Id, t.UnitPrice }).ToList()).ToListAsync()).Sum(x => x.Count), afficherSql: true);

// (2) EF : SQL brut composable (ROW_NUMBER)
await Mesurer("S3 EF FromSql ROW_NUMBER", async db =>
    (await db.Trades.FromSql($"""
        SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= 1000) x WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);

// (3) Fonction table : composable avec LINQ
await Mesurer("S3 fonction table", async db =>
    (await db.Database.SqlQuery<TopLigne>($"SELECT GoodId, Id, UnitPrice FROM dbo.Lab_Top3Echanges({1000})").ToListAsync()).Count, afficherSql: true);

// (4) Procédure stockée : non composable
await Mesurer("S3 procédure", async db =>
    (await db.Database.SqlQuery<TopLigne>($"EXEC dbo.Lab_Top3Proc {1000}").ToListAsync()).Count, afficherSql: true);

// (5) Dapper sur la connexion d'EF (paquet Dapper : using Dapper;) — les commandes Dapper n'apparaissent pas dans le compteur EF
await Mesurer("S3 Dapper", async db =>
{
    await db.Database.OpenConnectionAsync();
    var l = await db.Database.GetDbConnection().QueryAsync<TopLigne>(
        "/* lab:S3-dapper */ SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @max) x WHERE rn <= 3",
        new { max = 1000 });
    return l.Count();
});
// (6, facultatif) linq2db : même résultat avec Sql.Ext.RowNumber() — voir module 10, 10.2.4
```
**À observer**
- **Mêmes lignes** (vérifiez le résultat), **même plan, mêmes lectures logiques** (`SET STATISTICS IO, TIME ON` pour les formes SQL).
- Les écarts se lisent en **temps .NET** et en **allocations** (Dapper < EF) ; ils sont faibles devant le coût SQL.
- **LINQ contre procédure : pas de gain de plan.** Les vrais bénéfices d'une procédure sont organisationnels : droits par objet, traitements multi-étapes, exploitation (`sys.dm_exec_procedure_stats`). Ses coûts : non composable, SQL non vérifié à la compilation.
- **Avec l'index** `(GoodId, UnitPrice DESC)` le plan perd son tri pour **toutes** les techniques :
```sql
CREATE INDEX IX_Lab_Trades_Good_Price ON dbo.Trades (GoodId, UnitPrice DESC) INCLUDE (MarketId, CompanyId, TradeType, Quantity, TradedAt);
-- relancer les techniques ; puis :
DROP INDEX IX_Lab_Trades_Good_Price ON dbo.Trades;
```

**Arbre de décision** (à afficher) :
```
La requête est-elle lente ? → non : ne touchez à rien.
 └─ oui → l'avez-vous MESURÉE (SQL, mémoire, CPU) ? → non : mesurez.
          └─ oui → d'où vient le temps ?
              ├─ SQL (lectures, plan)        → index, projection, sargabilité, split… puis FromSql / TVF / vue
              ├─ transfert / matérialisation → projection, AsNoTracking, flux ; puis Dapper / ADO.NET
              ├─ pipeline EF (hot path mesuré) → requête compilée, pool ; puis Dapper
              ├─ écriture massive            → ExecuteUpdate/Delete, SqlBulkCopy, procédure ensembliste
              └─ expressivité (fenêtrage, CTE, MERGE) → FromSql / linq2db / procédure
```

### Synthèse (5 min)

**Les cinq réflexes**
1. **Mesurer avant d'optimiser** (SQL → mémoire → CPU ; `logical reads` d'abord).
2. **Ne pas déplacer de données inutiles** (projection, filtre, agrégat côté serveur).
3. **Écrire des prédicats sargables et les indexer** (et mesurer le coût d'écriture).
4. **Rester composable** (`IQueryable`, `Expression`, pas de matérialisation prématurée).
5. **Quitter LINQ par la mesure**, vers l'outil le plus étroit qui suffit.

Distribuer la **check-list de revue de code** (module 12) ; renvoyer à la **FAQ** [15](15-faq.md).

---

## Si l'on prend du retard

| Retard | À supprimer, dans cet ordre |
|---|---|
| 5 min | l'aperçu *sniffing* (bloc 6), la démo 6 (bloc 3) |
| 10 min | la démo 3 (bloc 2), la démo 9 (bloc 5) |
| 15 min | la démo 12 (bloc 7) → lecture seule ; mini-TP 2 |
| **jamais supprimé** | démos 1, 4, 7, 8, 11 et l'arbre de décision |

## Marges et variantes

- **2 h hors pause** : ajouter 10 min au **bloc 7** (columnstore, module 08, 8.5 ; ou verrous/isolation 8.13–8.14).
- **Version plus légère** (public moins aguerri) : supprimer l'aperçu *sniffing*, la démo 6 et la lecture de plan ; garder 8 démonstrations.
- **Version plus dense** : ajouter le top N par groupe en `APPLY` (module 07, 7.4.2) au bloc 6 et les limites d'`ExecuteUpdate` (module 05, 5.3) au bloc 7.

## Tableau à remplir par les participants

| # | Cas | Requêtes | Lignes lues | Logical reads | Mo alloués / pic | Seek ou scan | Commentaire |
|---|---|--:|--:|--:|--:|---|---|
| 1 | `Func` / `Expression` | | | | | | |
| 2 | Différé : 4 énumérations | | | | | | |
| 4 | Entité / projection | | | | | | |
| 7 | `ToLower`/`Year` : sans index / avec | | | | | | |
| 8 | `Include` ×3 / split / projection | | | | | | |
| 10 | Offset / keyset : sans / avec index | | | | | | |
| 11 | Flux avec suivi / sans | | | | | | |
| 12 | Un lot / plusieurs lots | | | | | | |
| 13 | Techniques de « top 3 par groupe » | | | | | | |

## Script de nettoyage (fin de séance)

```sql
DROP INDEX IF EXISTS IX_Lab_Starships_Cover   ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Starships_Cargo   ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt   ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt_Id ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Good_Price ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Planets_Population ON dbo.Planets;
DROP FUNCTION  IF EXISTS dbo.Lab_Top3Echanges;
DROP PROCEDURE IF EXISTS dbo.Lab_Top3Proc;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```

## À lire en autonomie après la séance

Dans l'ordre : modules **00 → 01 → 02 → 06 → 03 → 07 → 08 → 05 → 10 → 12 → 11** ; la **FAQ** ; le **déroulé de 5 h** ([13](13-deroule.md)) pour approfondir.
