# 10 — Quand sortir de LINQ, et vers quoi

> Suite de [`09-nouveautes.md`](09-nouveautes.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `SET STATISTICS IO, TIME ON`, plans) et `DansTransactionAnnulee` du [05](05-ecritures.md).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> **Principe du chapitre** : on ne quitte LINQ/EF Core **ni par réflexe ni par habitude**, mais parce qu'une **mesure** (les trois axes du 00) montre qu'aucune technique des chapitres 1 à 9 ne suffit. On compare **à périmètre identique** : mêmes lignes, mêmes colonnes, même résultat.
> Objets SQL : `Lab_`, index `IX_Lab_`. **Script de nettoyage** en fin de fichier.
> API de Dapper et de linq2db : recoupées avec la documentation pour la configuration de `linq2db` (`DataConnection` + `DataOptions.UseSqlServer`) ; le reste (requêtes fenêtrées, `BulkCopy`) est marqué *(à vérifier)* quand j'ai un doute sur la signature de votre version.

## Sommaire

- [Mise en place : le banc comparatif](#mise-en-place--le-banc-comparatif)
- **10.1 Les signaux d'alerte** : [10.1.1 fenêtrage/PIVOT/CTE/MERGE](#1011-fenêtrage-pivot-cte-récursive-merge) · [10.1.2 traitement ensembliste lourd](#1012-traitement-ensembliste-lourd) · [10.1.3 hints, tables temporaires, TVP](#1013-hints-tables-temporaires-tvp) · [10.1.4 SQL irréparable](#1014-sql-généré-irréparable) · [10.1.5 hot path](#1015-hot-path) · [10.1.6 export et reporting de masse](#1016-export-et-reporting-de-masse) · [10.1.7 insertion massive](#1017-insertion-massive)
- **10.2 Les options** : [10.2.1 `FromSql`/`SqlQuery`/`ExecuteSql`](#1021-fromsql-sqlquery-executesql) · [10.2.2 vues, fonctions table, procédures mappées](#1022-vues-fonctions-table-procédures) · [10.2.3 Dapper](#1023-dapper) · [10.2.4 linq2db](#1024-linq2db) · [10.2.5 `SqlBulkCopy`](#1025-sqlbulkcopy-et-bibliothèques-de-bulk) · [10.2.6 ADO.NET brut](#1026-adonet-brut-dbdatareader) · [10.2.7 autres micro-ORM](#1027-autres-micro-orm) · [10.2.8 CQRS](#1028-cqrs--écriture-ef-lecture-sql) · [10.2.9 moteur dédié](#1029-moteur-dédié)
- [10.3 Critères de décision](#103-critères-de-décision)
- [Script de nettoyage](#script-de-nettoyage)

---

## Mise en place : le banc comparatif

Paquets (dans votre projet) :

```bash
dotnet add package Dapper
dotnet add package linq2db
```

Quatre **scénarios** servent de fil rouge ; chaque notion rejoue celui qui la concerne avec **plusieurs techniques** :

| Scénario | Données | Techniques comparées |
|---|---|---|
| **S1 — Hot path** : 2 000 lectures d'un bien par `Id` | `Goods` | EF LINQ · EF compilé · Dapper · ADO.NET · procédure |
| **S2 — Export** : 2 M d'échanges vers un fichier | `Trades` | EF flux · Dapper non tamponné · ADO.NET · `bcp` |
| **S3 — Top 3 par groupe** (fenêtrage) | `Trades` | EF navigation · EF `FromSql` · Dapper · linq2db · procédure |
| **S4 — Insertion massive** : 100 000 lignes | `Lab_ItemBulk` | EF `AddRange` · linq2db `BulkCopy` · `SqlBulkCopy` · procédure |

Éléments communs à ajouter à `Program.cs` :

```csharp
using Dapper;
using LinqToDB; using LinqToDB.Data; using LinqToDB.Mapping;
using Microsoft.Data.SqlClient;
using System.Data;

public record EchangeLite(long Id, int GoodId, int Quantity, decimal UnitPrice);
public record BienLite(int Id, string Sku, string Name, int Category, decimal BasePrice, decimal WeightKg, bool IsContraband, bool IsPerishable);
public record TopLigne(int GoodId, long Id, decimal UnitPrice);

[Table("Trades")] public class TradeL2 { [PrimaryKey] public long Id { get; set; } [Column] public int GoodId { get; set; } [Column] public decimal UnitPrice { get; set; } }
```
**Mesure hors EF** : `Mesurer` donne temps, allocations et GC pour **n'importe quel code** ; mais **les compteurs de requêtes/lignes de `CompteurSql` ne voient que EF** (colonnes SQL à 0 pour Dapper, ADO.NET, linq2db). Pour le côté SQL, utilisez `SET STATISTICS IO, TIME ON` dans l'outil, ou un **commentaire-tag** dans le SQL (`/* lab:S1-dapper */ SELECT …`) et la requête sur `sys.dm_exec_query_stats` du 6.11.

---

# 10.1 Les signaux d'alerte

> Pour chaque signal : **comment le confirmer** (mesure), **ce qu'on essaie d'abord dans EF** (chapitres précédents), **quand on sort**.

## 10.1.1 Fenêtrage, `PIVOT`, CTE récursive, `MERGE`

**Axe concerné : 🗄️ SQL (expressivité).**

### La notion

Les **fonctions de fenêtrage** (`ROW_NUMBER`, `LAG`, `SUM() OVER`), `PIVOT`, les **CTE récursives** (arbres de factions) et `MERGE` n'ont **pas d'équivalent LINQ standard**. EF Core permet de les **injecter** (`FromSql`) tout en gardant la composition (10.2.1) ; hors de cela : Dapper, linq2db (qui expose le fenêtrage en LINQ), procédures.

### Dans quels cas on est concerné

Classements, différences avec la ligne précédente, cumuls glissants, hiérarchies (factions → branches), mise à jour ou insertion conditionnelle en une instruction.

### Exemple sans l'amélioration

```csharp
// ❌ Écart de prix avec l'échange précédent de chaque bien : en mémoire, après avoir tout lu
await Mesurer("AVANT : LAG en mémoire", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.GoodId <= 20_000).OrderBy(t => t.GoodId).ThenBy(t => t.TradedAt).Select(t => new { t.GoodId, t.UnitPrice }).ToListAsync();
    var n = 0; for (var i = 1; i < l.Count; i++) if (l[i].GoodId == l[i - 1].GoodId && l[i].UnitPrice > l[i - 1].UnitPrice) n++;
    return n;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ LAG en SQL, composé avec LINQ
public record Variation(int GoodId, decimal UnitPrice, decimal? Precedent);
await Mesurer("APRÈS : LAG via SqlQuery", async db =>
    (await db.Database.SqlQuery<Variation>($"""
        SELECT GoodId, UnitPrice, LAG(UnitPrice) OVER (PARTITION BY GoodId ORDER BY TradedAt) AS Precedent
        FROM dbo.Trades WHERE GoodId <= 20000
        """).Where(v => v.Precedent != null && v.UnitPrice > v.Precedent).CountAsync()), afficherSql: true);
```
**À observer** : lignes transférées (≈ 120 000 → 1 nombre) ; mémoire ; plan (un `Window Spool`/`Segment`).

### Sans index / avec index

Un index `(GoodId, TradedAt) INCLUDE (UnitPrice)` fournit déjà les lignes dans l'ordre de la partition : le plan perd son `Sort`.

### Comparaison avec une procédure stockée

Équivalent direct ; la procédure fige le SQL et protège l'accès aux tables.

### 🧪 TP 10.1.1 — « Arbre de factions »

*Énoncé.* `Factions.ParentFactionId` forme un arbre. Retournez toutes les sous-factions d'une faction racine (profondeur illimitée). Pourquoi LINQ ne le permet-il pas simplement ? Écrivez la CTE récursive en `SqlQuery`.

<details><summary>Correction</summary>

```csharp
public record SousFaction(int Id, int? ParentFactionId, int Profondeur);
var l = await db.Database.SqlQuery<SousFaction>($"""
    WITH arbre AS (
      SELECT Id, ParentFactionId, 0 AS Profondeur FROM dbo.Factions WHERE Id = {racine}
      UNION ALL
      SELECT f.Id, f.ParentFactionId, a.Profondeur + 1 FROM dbo.Factions f JOIN arbre a ON f.ParentFactionId = a.Id)
    SELECT Id, ParentFactionId, Profondeur FROM arbre
    """).ToListAsync();
```
LINQ n'a pas de récursion déclarative ; `Include` récursif ne peut pas aller « jusqu'au bout » d'un arbre de profondeur inconnue. Attention : une CTE ne se **compose pas** (elle ne s'enveloppe pas en sous-requête) : ajoutez `.ToListAsync()` après `SqlQuery`, ou isolez la CTE dans une vue/fonction table.
</details>

---

## 10.1.2 Traitement ensembliste lourd

**Axe concerné : 🗄️ SQL.** *Base : 5.1, 8.4.*

### La notion

Recalculs globaux, ETL, mises à jour qui lisent **et** écrivent des millions de lignes : le bon outil est **une instruction SQL ensembliste** (`INSERT … SELECT`, `UPDATE … FROM … JOIN`, `MERGE`). `ExecuteUpdate`/`ExecuteDelete` couvrent une partie ; au-delà (jointure entre tables, agrégat dans la mise à jour), **SQL brut ou procédure**.

### Exemple sans l'amélioration

```csharp
// ❌ Réévaluer le prix de base de 1 M de biens depuis la moyenne de leurs échanges : lire, boucler, sauver
await Mesurer("AVANT : boucle EF", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var moyennes = await db.Trades.Where(t => t.GoodId <= 20_000).GroupBy(t => t.GoodId).Select(g => new { g.Key, Moy = g.Average(t => t.UnitPrice) }).ToListAsync();
        foreach (var m in moyennes) { var g = await db.Goods.FindAsync(m.Key); g!.BasePrice = m.Moy; }
        return await db.SaveChangesAsync();
    }));
```

### Exemple avec l'amélioration

```csharp
// ✔ Une instruction UPDATE … FROM … JOIN
await Mesurer("APRÈS : UPDATE ensembliste", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE g SET g.BasePrice = m.Moy
            FROM dbo.Goods g JOIN (SELECT GoodId, AVG(UnitPrice) AS Moy FROM dbo.Trades WHERE GoodId <= 20000 GROUP BY GoodId) m ON m.GoodId = g.Id
            """)));
```
**À observer** : une seule requête contre 20 000 `Find` + `UPDATE` ; le journal ; le temps.

### Sans index / avec index

`IX_Trades_GoodId` (FK) sert l'agrégat ; un index couvrant `(GoodId) INCLUDE (UnitPrice)` l'accélère.

### Comparaison avec une procédure stockée

Une procédure est **le** réceptacle naturel d'un traitement planifié (job) : droits, versionnement, plan en cache.

### 🧪 TP 10.1.2 — « Traiter par lots »

*Énoncé.* Transformez le `UPDATE … JOIN` en traitement **par plages de `GoodId`** (lots de 5 000) pour limiter verrous et journal (voir 8.2). Écrivez-le en procédure avec boucle.

<details><summary>Correction</summary>

Boucle `WHILE` sur `@Debut` avec `UPDATE … WHERE g.Id >= @Debut AND g.Id < @Debut + 5000`, `COMMIT` à chaque tour ; la plage de clé garantit un *seek* par lot.
</details>

---

## 10.1.3 Hints, tables temporaires, TVP

**Axe concerné : 🗄️ SQL (contrôle du plan).** *Base : 7.4.6, 7.5.1.*

### La notion

Quand l'optimiseur choisit un mauvais plan et qu'aucun index/statistique ne le corrige, on peut **orienter** : hints (`OPTION (...)`), **tables temporaires** (matérialiser un résultat intermédiaire avec statistiques), **paramètres table (TVP)** pour passer de grandes listes. LINQ n'a aucun de ces outils nativement ; l'intercepteur (7.5.1) couvre les hints simples, le reste est du SQL.

### Exemple

```sql
-- Table temporaire pour un traitement en deux étapes
SELECT GoodId, AVG(UnitPrice) AS Moy INTO #moy FROM dbo.Trades WHERE TradedAt >= '2025-01-01' GROUP BY GoodId;
CREATE CLUSTERED INDEX ix ON #moy (GoodId);
SELECT g.Id, g.Name, m.Moy FROM dbo.Goods g JOIN #moy m ON m.GoodId = g.Id WHERE m.Moy > 90000;
```
```csharp
// En EF : tout dans une seule requête SQL brute (une table temporaire n'existe que sur la connexion : ouvrez-la explicitement)
await db.Database.OpenConnectionAsync();
await db.Database.ExecuteSqlAsync($"SELECT GoodId, AVG(UnitPrice) AS Moy INTO #moy FROM dbo.Trades WHERE TradedAt >= {new DateTime(2025,1,1)} GROUP BY GoodId");
var l = await db.Database.SqlQuery<(int Id, string Name, decimal Moy)>($"SELECT g.Id, g.Name, m.Moy FROM dbo.Goods g JOIN #moy m ON m.GoodId = g.Id WHERE m.Moy > 90000").ToListAsync();
```
*(Le type tuple passé à `SqlQuery` peut ne pas être pris en charge : utilisez un `record` si votre version le refuse.)*

**À observer** : le plan de la requête avec **statistiques** sur la table temporaire (estimation correcte) contre la même requête **en une passe** avec une estimation erronée ; le **coût** de la matérialisation.

### Sans index / avec index et procédure stockée

L'index de la table temporaire (`CREATE CLUSTERED INDEX ix ON #moy`) est la partie « avec index ». Une procédure est le cadre habituel de ces traitements en plusieurs étapes.

### 🧪 TP 10.1.3 — « Deux étapes contre une »

*Énoncé.* Écrivez une requête volumineuse en deux étapes avec table temporaire et comparez avec la version monolithique : estimations dans le plan, lectures, temps.

<details><summary>Correction</summary>

La version en deux étapes gagne quand l'optimiseur sous-estime la taille d'un résultat intermédiaire ; elle perd quand la matérialisation coûte plus que l'erreur. **Mesurez** : ce n'est pas une règle générale.
</details>

---

## 10.1.4 SQL généré irréparable

**Axe concerné : diagnostic.**

### La notion

« Irréparable » signifie : **après** avoir essayé projection, index, split, compilation, `FromSql` partiel, **le SQL généré reste inadapté** (forme imposée par la traduction EF). Avant de conclure, vérifiez : (1) le plan **réel** (pas une impression), (2) les statistiques à jour, (3) si une réécriture LINQ change réellement le SQL (`ToQueryString`), (4) si `FromSql` d'**une partie** suffit.

### Exemple (démarche)

```csharp
var q = db.Planets.Where(p => p.Markets.Any(m => m.IsBlackMarket) && p.Inhabitants.Any(i => i.IsActive));
Console.WriteLine(q.ToQueryString());
```
```sql
SET STATISTICS IO, TIME ON;                   -- puis le plan réel de ce SQL, copié-collé
UPDATE STATISTICS dbo.Planets WITH FULLSCAN;  -- éliminer l'hypothèse « statistiques périmées »
```
**À observer** : si le plan n'est mauvais qu'à cause d'une estimation ; si la **même requête écrite à la main** (variante SQL) obtient un meilleur plan — alors le problème est la **forme** du SQL EF, pas le moteur.

### Sans index / avec index et procédure stockée

Un index manquant n'est **pas** un SQL irréparable : testez-le avant.

### 🧪 TP 10.1.4 — « Prouver avant de quitter »

*Énoncé.* Pour une requête lente de votre choix, rédigez le **dossier** : requête LINQ, SQL, plan, tentatives (index, projection, split…) avec leur effet mesuré, version SQL réécrite et son gain. Quand le dossier justifie-t-il la sortie de LINQ ?

<details><summary>Correction</summary>

Quand le SQL réécrit à la main gagne **de façon mesurée et durable** et qu'aucune réécriture LINQ n'obtient le même plan. Sans ce dossier, la sortie de LINQ est une préférence, pas une décision d'ingénierie.
</details>

---

## 10.1.5 Hot path

**Axe concerné : ⚙️ .NET (CPU, allocations). ** *Base : 0.4, 7.1.2.*

### La notion

Sur un chemin appelé **des milliers de fois par seconde**, le coût du pipeline EF (quelques dizaines de µs et quelques Ko par appel) devient visible. Options : requête compilée (7.1.2), pool (7.1.5), puis **Dapper/ADO.NET** pour les cas extrêmes.

### Exemple sans l'amélioration

```csharp
// S1 — AVANT : EF LINQ, 2 000 lectures par clé
await Mesurer("S1 EF normal", async db => { long s = 0; for (var i = 1; i <= 2000; i++) s += (await db.Goods.AsNoTracking().FirstAsync(g => g.Id == i)).Id; return s; });
```

### Exemple avec l'amélioration

```csharp
// S1 — EF compilé (7.1.2)
await Mesurer("S1 EF compilé", async db => { long s = 0; for (var i = 1; i <= 2000; i++) s += (await CompileesS1.BienParId(db, i))!.Id; return s; });
// S1 — Dapper
await Mesurer("S1 Dapper", async _ =>
{
    await using var cn = new SqlConnection(Cs); await cn.OpenAsync(); long s = 0;
    for (var i = 1; i <= 2000; i++) s += (await cn.QuerySingleAsync<BienLite>("/* lab:S1-dapper */ SELECT Id, Sku, Name, Category, BasePrice, WeightKg, IsContraband, IsPerishable FROM dbo.Goods WHERE Id = @id", new { id = i })).Id;
    return s;
});
// S1 — ADO.NET brut
await Mesurer("S1 ADO.NET", async _ =>
{
    await using var cn = new SqlConnection(Cs); await cn.OpenAsync();
    await using var cmd = new SqlCommand("/* lab:S1-ado */ SELECT Id, Sku, Name, Category, BasePrice, WeightKg, IsContraband, IsPerishable FROM dbo.Goods WHERE Id = @id", cn);
    var p = cmd.Parameters.Add("@id", SqlDbType.Int); long s = 0;
    for (var i = 1; i <= 2000; i++) { p.Value = i; await using var r = await cmd.ExecuteReaderAsync(); if (await r.ReadAsync()) s += r.GetInt32(0); }
    return s;
});
```
(avec, à côté de `Program.cs`, `public static class CompileesS1 { public static readonly Func<StarWarsContext, int, Task<Good?>> BienParId = EF.CompileAsyncQuery((StarWarsContext c, int id) => c.Goods.AsNoTracking().FirstOrDefault(g => g.Id == id)); }`)

**À observer** : le temps par appel et les allocations **par technique** ; l'écart relatif au temps d'un aller-retour SQL (le SQL est trivial, l'écart mesure le **pipeline**) ; le fait que Dapper et ADO.NET partagent à peu près le même coût sur ce scénario.

### Sans index / avec index

Lecture par clé primaire : seek dans tous les cas.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_BienParId @Id int AS BEGIN SET NOCOUNT ON; SELECT Id, Sku, Name, Category, BasePrice, WeightKg, IsContraband, IsPerishable FROM dbo.Goods WHERE Id = @Id; END
```
```csharp
await Mesurer("S1 Dapper + procédure", async _ =>
{ await using var cn = new SqlConnection(Cs); await cn.OpenAsync(); long s = 0;
  for (var i = 1; i <= 2000; i++) s += (await cn.QuerySingleAsync<BienLite>("dbo.Lab_BienParId", new { Id = i }, commandType: CommandType.StoredProcedure)).Id; return s; });
```
**À observer** : aucun gain notable sur la requête simple (même plan) ; la procédure n'est pas un accélérateur de requêtes triviales.

### 🧪 TP 10.1.5 — « À partir de quand quitter EF ? »

*Énoncé.* Remplissez : technique / µs par appel / Ko alloués par appel, pour S1. Calculez le **gain annuel** de CPU pour un service qui fait 1 000 appels par seconde. Conclure sur la pertinence de quitter EF pour un tel chemin.

<details><summary>Correction</summary>

Si l'écart est de quelques dizaines de µs par appel, 1 000 appels/s économisent quelques dizaines de millisecondes de CPU **par seconde** : rarement décisif. La sortie d'EF se justifie quand l'écart multiplié par la fréquence **sature** un cœur ou un budget de latence mesuré.
</details>

---

## 10.1.6 Export et reporting de masse

**Axe concerné : 🧠 mémoire + ⚙️ .NET + 🗄️ SQL.** *Base : 8.1.*

### La notion

Lire des millions de lignes pour les écrire dans un fichier ou les envoyer ailleurs : le coût d'**objets** (même non suivis) domine. Techniques : flux EF (8.1) → Dapper **non tamponné** → `SqlDataReader` brut → `bcp`/export côté SQL.

### Exemple sans l'amélioration

```csharp
// S2 — AVANT : entités EF en flux
await Mesurer("S2 EF flux", async db =>
{ var n = 0; await using var w = new StreamWriter(Path.GetTempFileName());
  await foreach (var t in db.Trades.AsNoTracking().Where(t => t.Id <= 2_000_000).AsAsyncEnumerable()) { await w.WriteLineAsync($"{t.Id};{t.GoodId};{t.Quantity};{t.UnitPrice}"); n++; } return n; });
```

### Exemple avec l'amélioration

```csharp
// S2 — Dapper non tamponné (API asynchrone en flux : à vérifier dans votre version de Dapper)
await Mesurer("S2 Dapper non tamponné", async _ =>
{ var n = 0; await using var cn = new SqlConnection(Cs); await cn.OpenAsync(); await using var w = new StreamWriter(Path.GetTempFileName());
  await foreach (var t in cn.QueryUnbufferedAsync<EchangeLite>("/* lab:S2-dapper */ SELECT Id, GoodId, Quantity, UnitPrice FROM dbo.Trades WHERE Id <= 2000000"))
  { await w.WriteLineAsync($"{t.Id};{t.GoodId};{t.Quantity};{t.UnitPrice}"); n++; } return n; });

// S2 — ADO.NET brut, accès séquentiel
await Mesurer("S2 ADO.NET", async _ =>
{ var n = 0; await using var cn = new SqlConnection(Cs); await cn.OpenAsync(); await using var w = new StreamWriter(Path.GetTempFileName());
  await using var cmd = new SqlCommand("/* lab:S2-ado */ SELECT Id, GoodId, Quantity, UnitPrice FROM dbo.Trades WHERE Id <= 2000000", cn);
  await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
  while (await r.ReadAsync()) { await w.WriteLineAsync($"{r.GetInt64(0)};{r.GetInt32(1)};{r.GetInt32(2)};{r.GetDecimal(3)}"); n++; } return n; });
```
```bash
# S2 — export côté SQL, sans .NET (voir 8.1)
docker exec masteringlinq-sql /opt/mssql-tools18/bin/bcp "SELECT Id, GoodId, Quantity, UnitPrice FROM StarWarsTrade.dbo.Trades WHERE Id <= 2000000" queryout /tmp/t.csv -c -t ";" -S localhost -U sa -P 'StarWars_Pwd123!' -C
```
**À observer** : allocations par technique (EF alloue une entité par ligne ; ADO.NET presque rien) ; temps ; écriture du fichier (souvent le goulot : mesurez avec/sans écriture).

### Sans index / avec index

Lecture séquentielle du clustered : aucun index. Pour un export **trié autrement**, il faudrait un index sur l'ordre (voir 7.3.1).

### Comparaison avec une procédure stockée

Une procédure de lecture ne change rien au coût de **transfert** ; `bcp`/`BULK`/exports côté serveur évitent le transfert .NET.

### 🧪 TP 10.1.6 — « Le goulot de l'export »

*Énoncé.* Mesurez S2 avec les quatre techniques **sans écrire de fichier** (seulement compter), puis avec. Où est le goulot ?

<details><summary>Correction</summary>

Sans écriture, vous mesurez le coût **lecture + matérialisation** ; avec, vous ajoutez les E/S fichier et le formatage de chaînes. Souvent, le formatage (`$"{…}"`) et l'écriture dominent : optimisez-les (tampon, `Utf8Formatter`, écriture par blocs) avant de changer d'ORM.
</details>

---

## 10.1.7 Insertion massive

**Axe concerné : 🗄️ SQL (journal) + ⚙️ .NET.** *Base : 5.5.*

### La notion

Voir 5.5 : `SqlBulkCopy`, `INSERT … SELECT`, bibliothèques de bulk. Sur 100 000 lignes et plus, `AddRange` + `SaveChanges` est la solution **la plus lente** (suivi + commandes par lot).

### Exemple (S4, rappel et extension linq2db)

```sql
CREATE TABLE dbo.Lab_ItemBulk (Id int IDENTITY(1,1) PRIMARY KEY, Nom nvarchar(30) NOT NULL, Valeur int NOT NULL);
```
```csharp
[Table("Lab_ItemBulk")] public class ItemBulk { [PrimaryKey, Identity] public int Id { get; set; } [Column] public string Nom { get; set; } = ""; [Column] public int Valeur { get; set; } }

var lignes = Enumerable.Range(0, 100_000).Select(i => new ItemBulk { Nom = $"x{i}", Valeur = i }).ToList();
await Mesurer("S4 linq2db BulkCopy", async _ =>
{
    await using var dc = new DataConnection(new DataOptions().UseSqlServer(Cs));
    await using var tx = await dc.BeginTransactionAsync();
    var r = await dc.GetTable<ItemBulk>().BulkCopyAsync(lignes);                      // (à vérifier : signature selon votre version)
    await tx.RollbackAsync(); return r.RowsCopied;
});
```
Les trois autres techniques (EF `AddRange`, `SqlBulkCopy` du 5.5, procédure `INSERT … SELECT … GENERATE_SERIES`) sont dans le 05.

**À observer** : temps et allocations pour les quatre ; **index sur la table** ; journalisation minimale (`TableLock`).

### Sans index / avec index

Plus d'index = insertion plus lente pour **toutes** les techniques (5.5).

### Comparaison avec une procédure stockée

La procédure ensembliste est la borne basse **si** les données sont déjà en base ; sinon, `SqlBulkCopy`.

### 🧪 TP 10.1.7 — « Le meilleur chemin pour 1 M de lignes »

*Énoncé.* Classez les quatre techniques pour 1 M de lignes avec deux index non clusterisés sur la table. Que changez-vous si les lignes arrivent par un **flux** (API) plutôt que d'une liste ?

<details><summary>Correction</summary>

Pour un flux, utilisez `SqlBulkCopy` avec un `IDataReader` adapté ou des lots (10 000) pour borner la mémoire ; désactivez/reconstruisez les index non clusterisés pour les très gros chargements.
</details>

---

# 10.2 Les options

## 10.2.1 `FromSql`, `SqlQuery`, `ExecuteSql`

**Axe concerné : 🗄️ SQL, sans quitter EF.** *Base : 5.6, 5.7.*

### La notion

La **première** option est de **rester dans EF** et d'écrire du SQL pour la seule partie qui l'exige : `FromSql` (entités), `SqlQuery<T>` (DTO/scalaires), `ExecuteSql` (écritures). Résultat **composable** (enveloppé en sous-requête) si c'est un `SELECT`. **Interpolation = paramètres** (jamais de concaténation).

### Exemple (S3 — top 3 par bien)

```csharp
// S3 — EF navigation (7.4.2) vs FromSql ROW_NUMBER
await Mesurer("S3 EF navigation", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 1_000)
        .Select(g => g.Trades.OrderByDescending(t => t.UnitPrice).Take(3).Select(t => new { t.GoodId, t.Id, t.UnitPrice }).ToList()).ToListAsync()).Sum(x => x.Count), afficherSql: true);

await Mesurer("S3 EF FromSql ROW_NUMBER", async db =>
    (await db.Trades.FromSql($"""
        SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= 1000) x WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);
```
**À observer** : plans, lectures, SQL (composition), temps ; index `(GoodId, UnitPrice DESC)` (2.13).

### Sans index / avec index et procédure stockée

Voir 2.13 : l'index aligné sur la partition supprime le tri.

### 🧪 TP 10.2.1 — « SQL brut, mais sûr »

*Énoncé.* Écrivez une fonction `RechercherBiens(string motif, decimal prixMax)` en `FromSql` qui **ne peut pas** être injectée. Montrez ce que fait un `motif = "'; DROP TABLE Goods;--"`.

<details><summary>Correction</summary>

`db.Goods.FromSql($"SELECT * FROM dbo.Goods WHERE Name LIKE {motif + "%"} AND BasePrice < {prixMax}")` : le motif est un **paramètre** (`@p0`), la chaîne malveillante est traitée comme une valeur. Avec `FromSqlRaw("… " + motif)`, elle serait exécutée.
</details>

---

## 10.2.2 Vues, fonctions table, procédures

**Axe concerné : 🗄️ SQL (logique dans la base).** *Base : 2.4, 4.7.*

### La notion

| Objet | Composable depuis LINQ ? | Usage |
|---|---|---|
| **Vue** (`ToView`) | **oui** | requête nommée réutilisable ; vue indexée = pré-agrégat |
| **Fonction table** (`[DbFunction]`) | **oui** | requête paramétrée composable |
| **Procédure** (`SqlQuery`/`FromSql EXEC`) | **non** | traitement multi-étapes, droits, planification |

### Exemple (S3 en vue/TVF/procédure)

```sql
CREATE OR ALTER FUNCTION dbo.Lab_Top3Echanges (@GoodMax int) RETURNS TABLE AS RETURN
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3;
GO
CREATE OR ALTER PROCEDURE dbo.Lab_Top3Proc @GoodMax int AS BEGIN SET NOCOUNT ON;
  SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @GoodMax) x WHERE rn <= 3; END
```
```csharp
await Mesurer("S3 TVF composable", async db =>
    (await db.Database.SqlQuery<TopLigne>($"SELECT GoodId, Id, UnitPrice FROM dbo.Lab_Top3Echanges({1000})").Where(t => t.UnitPrice > 50_000m).ToListAsync()).Count, afficherSql: true);
await Mesurer("S3 procédure", async db =>
    (await db.Database.SqlQuery<TopLigne>($"EXEC dbo.Lab_Top3Proc {1000}").ToListAsync()).Count, afficherSql: true);
```
**À observer** : la TVF se **compose** (le `Where` part dans le SQL) alors que la procédure retourne tout ; les plans sont identiques à même requête.

### Sans index / avec index

Index `(GoodId, UnitPrice DESC)` : bénéfice identique pour les trois.

### Comparaison avec une procédure stockée — **tableau de décision**

| | LINQ (EF) | `FromSql` | Vue / TVF | Procédure |
|---|---|---|---|---|
| Composable | oui | oui (SELECT) | oui | **non** |
| Typage / refactoring | compilateur | partiel | partiel | texte SQL |
| Plan / performance | idem à SQL équivalent | idem | idem | idem |
| Droits d'accès | sur les tables | sur les tables | sur l'objet | **sur l'objet** |
| Multi-étapes, temp tables | non | non | non | **oui** |
| Versionnement | code | code | base | base |
| Hints / `RECOMPILE` | intercepteur | intercepteur | oui | **oui** |

### 🧪 TP 10.2.2 — « Procédure ou TVF ? »

*Énoncé.* Pour un top 3 par bien que l'écran filtre ensuite par catégorie de bien et pagine, choisissez entre procédure et TVF. Justifiez avec la composabilité et mesurez.

<details><summary>Correction</summary>

TVF : le filtre/pagination s'ajoutent côté SQL ; avec une procédure, le filtre par catégorie se fait en mémoire sur le résultat complet (ou doit devenir un paramètre de la procédure).
</details>

---

## 10.2.3 Dapper

**Axe concerné : ⚙️ .NET (matérialisation légère) + SQL maîtrisé.**

### La notion

Dapper est un **micro-mapper** : vous écrivez le SQL, il exécute et **mappe** le résultat vers des objets. Aucun suivi, pas de génération de SQL, très peu de surcoût. Il complète EF (même connexion, même transaction) plutôt qu'il ne le remplace. Limites : SQL en chaînes (pas de vérification à la compilation), aucune composition, pas de gestion des relations ni des migrations.

### Dans quels cas on est concerné

Lectures critiques, requêtes complexes déjà au point en SQL, rapports.

### Exemple (S3 en Dapper, sur la **connexion d'EF**)

```csharp
await Mesurer("S3 Dapper", async db =>
{
    var cn = db.Database.GetDbConnection();                                 // partage la connexion (et la transaction éventuelle) d'EF
    await db.Database.OpenConnectionAsync();
    var l = await cn.QueryAsync<TopLigne>("""
        /* lab:S3-dapper */ SELECT GoodId, Id, UnitPrice FROM (SELECT GoodId, Id, UnitPrice, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) rn FROM dbo.Trades WHERE GoodId <= @max) x WHERE rn <= 3
        """, new { max = 1000 });
    return l.Count();
});
```
**À observer** : même plan que `FromSql` ; allocations légèrement inférieures ; **aucun** SQL visible dans le journal EF (les commandes Dapper n'y passent pas) : utilisez le commentaire-tag et `sys.dm_exec_query_stats`.

### Sans index / avec index

Mêmes index que pour toute technique.

### Comparaison avec une procédure stockée

Dapper appelle les procédures (`commandType: StoredProcedure`) avec la même facilité ; la combinaison *Dapper + procédure* est un classique.

### 🧪 TP 10.2.3 — « Dapper dans la transaction d'EF »

*Énoncé.* Dans une transaction EF, écrivez une ligne avec EF et lisez-la avec Dapper (même transaction). Que se passe-t-il si Dapper n'utilise pas la transaction ?

<details><summary>Correction</summary>

Passez `transaction: db.Database.CurrentTransaction!.GetDbTransaction()` à Dapper : il voit la ligne non validée. Sans, il utilise la connexion mais **hors** de la transaction (erreur « la commande doit être associée à la transaction » sur SqlClient).
</details>

---

## 10.2.4 linq2db

**Axe concerné : 🗄️ SQL (LINQ plus riche) + ⚙️ .NET.**

### La notion

linq2db traduit du **LINQ en SQL** comme EF, mais **sans suivi**, avec un **DSL de fenêtrage** (`Sql.Ext`), `MERGE`, hints de table/requête, `BulkCopy` et mises à jour/suppressions ensemblistes natives. Mappage par attributs ou configuration. Moins d'abstraction relationnelle qu'EF (pas de graphe d'entités, pas de migrations).

### Dans quels cas on est concerné

Quand vous voulez **rester en LINQ** mais que le fenêtrage, `MERGE`, les hints ou le bulk l'exigent.

### Exemple (S3 en linq2db)

```csharp
await Mesurer("S3 linq2db", async _ =>
{
    await using var dc = new DataConnection(new DataOptions().UseSqlServer(Cs));
    var q = from t in dc.GetTable<TradeL2>().Where(t => t.GoodId <= 1000)
            select new { t.GoodId, t.Id, t.UnitPrice, Rn = Sql.Ext.RowNumber().Over().PartitionBy(t.GoodId).OrderByDesc(t.UnitPrice).ToValue() };
    var l = await q.Where(x => x.Rn <= 3).ToListAsync();                     // (à vérifier : API de fenêtrage selon votre version)
    return l.Count;
});
```
**À observer** : le SQL généré (`dc.LastQuery` ou `q.ToString()`) : un `ROW_NUMBER() OVER (…)` dans une sous-requête ; le plan **identique** au SQL écrit à la main.

### Sans index / avec index

Comme toutes les options : l'index `(GoodId, UnitPrice DESC)` s'applique.

### Comparaison avec une procédure stockée

Une procédure **cache** le SQL ; linq2db le **génère** en gardant la vérification à la compilation.

### 🧪 TP 10.2.4 — « Le même résultat, trois façons »

*Énoncé.* Écrivez S3 avec EF `FromSql`, Dapper et linq2db. Vérifiez que les trois retournent les mêmes lignes ; comparez lisibilité, SQL, temps, allocations. Laquelle recommandez-vous pour un projet qui a déjà EF ?

<details><summary>Correction</summary>

Pour un projet EF existant : `FromSql`/`SqlQuery` pour un cas isolé (aucune dépendance de plus) ; linq2db si les besoins de ce type se **multiplient** et que vous voulez rester en LINQ ; Dapper si vous préférez du SQL écrit et une API minimale.
</details>

---

## 10.2.5 `SqlBulkCopy` et bibliothèques de bulk

**Axe concerné : 🗄️ SQL (journal) + ⚙️ .NET.** *Base : 5.5.*

### La notion

`SqlBulkCopy` envoie un **flux** de lignes au serveur en protocole *bulk* : de loin le plus rapide pour de gros volumes, avec possibilité de **journalisation minimale**. Des bibliothèques (dont celles d'extensions EF) ajoutent mise à jour et suppression en masse ; **vérifiez leur compatibilité avec votre version d'EF Core** avant de les adopter. Limites : pas de suivi, valeurs générées non rapatriées, transaction à gérer.

### Exemple

Voir **5.5** (code `SqlBulkCopy` avec transaction EF) et **10.1.7** (linq2db `BulkCopy`).

### Sans index / avec index et procédure stockée

Voir 5.5 : index et `TableLock` ; la procédure `INSERT … SELECT` est la borne basse **côté serveur**.

### 🧪 TP 10.2.5 — « Bulk avec reprise »

*Énoncé.* Écrivez un chargement de 1 M de lignes par `SqlBulkCopy` avec lots de 100 000 (`BatchSize`) et traitement d'une panne au lot 7. Qu'est-ce qui est validé, qu'est-ce qui ne l'est pas ?

<details><summary>Correction</summary>

Sans transaction explicite, chaque lot est validé séparément : les lots 1 à 6 restent. Avec une transaction externe, tout est annulé. Choisissez selon que le chargement est **repris** ou **rejoué**.
</details>

---

## 10.2.6 ADO.NET brut (`DbDataReader`)

**Axe concerné : ⚙️ .NET (surcoût minimal).**

### La notion

`SqlCommand` + `SqlDataReader` : **le plus bas niveau**. Aucune allocation d'objet par ligne, accès par ordinal (`GetInt64(0)`), `SequentialAccess` pour les colonnes volumineuses. Vous écrivez le mappage **à la main** : fastidieux, mais c'est la borne basse de ce qu'un client .NET peut obtenir.

### Dans quels cas on est concerné

Chemins les plus chauds, exports de masse, colonnes `max` en flux.

### Exemple

Voir S1 (10.1.5) et S2 (10.1.6). **Lire un gros champ en flux** :

```csharp
await using var cmd = new SqlCommand("SELECT Contenu FROM dbo.Lab_Blob WHERE Id = @id", cn);   // table du 7.7.2
cmd.Parameters.AddWithValue("@id", 1);
await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
if (await r.ReadAsync()) { using var tr = r.GetTextReader(0); var tampon = new char[8192]; var total = 0; int n; while ((n = await tr.ReadAsync(tampon)) > 0) total += n; }
```

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 10.2.6 — « Borne basse »

*Énoncé.* Sur S2, mesurez ADO.NET avec un `StringBuilder` formatant chaque ligne, puis avec une écriture binaire directe. Quel est le plancher de temps de lecture pure ?

<details><summary>Correction</summary>

Le plancher se mesure en **lisant sans rien faire** de chaque ligne (juste `ReadAsync`) : tout temps au-dessus est du **traitement** (formatage, écriture), pas de la lecture.
</details>

---

## 10.2.7 Autres micro-ORM

**Axe concerné : choix d'outil.**

### La notion

RepoDB, PetaPoco, NPoco, OrmLite, etc. appartiennent à la même famille que Dapper (SQL ou DSL léger, pas de suivi) avec des options en plus (opérations de lot, caches). **Je n'ai installé aucun de ces paquets** : la comparaison est **conceptuelle** ; pour l'objectiver, rejouez S1 et S2 avec le micro-ORM visé et comparez à Dapper/ADO.NET.

### Critères de comparaison

| Critère | À mesurer / vérifier |
|---|---|
| Coût par ligne | S2 (allocations, temps) |
| Mappage | record/constructeur, colonnes manquantes |
| Opérations de lot | S4 |
| Écosystème | maintenance, compatibilité .NET, communauté |
| Intégration EF | partage de connexion/transaction |

### 🧪 TP 10.2.7 — « Ajouter un outil, justifier »

*Énoncé.* Choisissez un micro-ORM, écrivez S1 et S2 avec lui, et tracez : temps, allocations, lignes de code, ergonomie. Est-ce que le gain par rapport à Dapper justifie une dépendance de plus ?

<details><summary>Correction</summary>

La règle : une dépendance supplémentaire se justifie par un **gain mesuré sur votre charge**, pas par un benchmark générique ; sinon, restez sur Dapper (largement connu) ou sur ADO.NET.
</details>

---

## 10.2.8 CQRS : écriture EF, lecture SQL

**Axe concerné : architecture.**

### La notion

On **sépare** le chemin d'écriture (EF Core : modèle riche, suivi, transactions) du chemin de lecture (**modèle de lecture** optimisé : vues, tables dénormalisées, Dapper/SQL). On accepte une **duplication** pour que chaque côté soit simple et rapide.

### Dans quels cas on est concerné

Écrans de consultation lourds sur un modèle d'écriture normalisé ; charges de lecture ≫ écriture.

### Exemple

```sql
-- Modèle de lecture : une fiche planète à plat (vue)
CREATE OR ALTER VIEW dbo.Lab_vwFichePlanete AS
SELECT p.Id, p.Name AS Planete, ss.Name AS Systeme, s.Name AS Secteur,
       (SELECT COUNT(*) FROM dbo.Markets m WHERE m.PlanetId = p.Id) AS NbMarches,
       (SELECT COUNT(*) FROM dbo.Inhabitants i WHERE i.PlanetId = p.Id) AS NbHabitants
FROM dbo.Planets p JOIN dbo.StarSystems ss ON ss.Id = p.StarSystemId JOIN dbo.Sectors s ON s.Id = ss.SectorId;
```
```csharp
public record FichePlanete(int Id, string Planete, string Systeme, string Secteur, int NbMarches, int NbHabitants);
// Côté lecture : Dapper ou SqlQuery, aucun suivi
await Mesurer("Lecture : vue + Dapper", async db =>
    (await (await OuvrirAsync(db)).QueryAsync<FichePlanete>("SELECT * FROM dbo.Lab_vwFichePlanete WHERE Id BETWEEN 1 AND 2000")).Count());
// Côté écriture : EF normal (suivi, validation, transaction)
```
(`OuvrirAsync` : petite fonction qui retourne `db.Database.GetDbConnection()` après `OpenConnectionAsync`.)

**À observer** : comparaison avec la même fiche en LINQ à 3 navigations + 2 agrégats corrélés (7.4.4) ; lectures ; code. Coût du modèle de lecture : **maintenance** (la vue suit le modèle d'écriture), **cohérence** (si la lecture est une table dénormalisée alimentée par lots).

### Sans index / avec index

Le modèle de lecture permet des **index dédiés** sans alourdir l'écriture du modèle principal (index de la vue, table de lecture indexée à part).

### Comparaison avec une procédure stockée

Les vues et procédures **sont** souvent le modèle de lecture dans les systèmes CQRS sur SQL ; la différence avec LINQ est la **propriété** du modèle (équipe base de données ou équipe applicative).

### 🧪 TP 10.2.8 — « Cohérence éventuelle »

*Énoncé.* Votre modèle de lecture est une table remplie chaque nuit. Un utilisateur crée un marché et consulte aussitôt la fiche : que voit-il ? Proposez deux solutions (lecture dans le modèle d'écriture pour l'auteur, rafraîchissement ciblé).

<details><summary>Correction</summary>

Il ne voit pas son marché (cohérence éventuelle). Solutions : rediriger **sa** lecture vers le modèle d'écriture après une écriture ; mettre à jour la ligne concernée du modèle de lecture dans la même transaction ; ou accepter et l'afficher.
</details>

---

## 10.2.9 Moteur dédié

**Axe concerné : architecture (hors périmètre SQL Server).**

### La notion

Des besoins différents appellent un moteur différent : **analytique colonnaire** (agrégats sur milliards de lignes), **recherche plein texte** (facettes, pertinence), **cache**, **séries temporelles**. Dans un environnement SQL Server, la première étape est de vérifier ce que **le moteur actuel** sait déjà faire : **columnstore** (8.5), **plein texte** (2.3), **vues indexées** (4.7). Un moteur externe se justifie quand ces outils ne suffisent plus **et** qu'on accepte la **synchronisation** (délai, double écriture, reprise).

### Critères

| Besoin | D'abord essayer dans SQL Server | Moteur dédié si… |
|---|---|---|
| Agrégats analytiques | columnstore, vues indexées | volumes ou concurrence que le serveur OLTP ne peut absorber |
| Recherche textuelle | plein texte | facettes, tolérance aux fautes, classement de pertinence |
| Cache de lecture | vue / table de lecture | latence très basse à grande échelle |
| Recherche sémantique | type `vector` (SQL Server 2025, 9.10) | volumes ou fonctions absents du serveur |

### 🧪 TP 10.2.9 — « Dossier d'architecture »

*Énoncé.* Pour un tableau de bord analytique sur `Trades` à 5 000 requêtes/jour : rédigez le dossier qui compare columnstore dans SQL Server et un moteur externe (coût, délai de fraîcheur, complexité, risques). Quelle décision et sur quelles mesures ?

<details><summary>Correction</summary>

Mesurez d'abord le columnstore (8.5) sur la charge réelle ; si les temps et l'impact sur l'OLTP sont acceptables, **il n'y a aucune raison** d'ajouter un moteur. Le dossier doit contenir ces mesures — c'est ce qui rend la décision défendable.
</details>

---

# 10.3 Critères de décision

### Arbre de décision

```
La requête est-elle lente ?
 ├─ Non  → ne touchez à rien.
 └─ Oui → l'avez-vous MESURÉE (3 axes : SQL, mémoire, CPU) ?
          ├─ Non → mesurez (chapitre 00).
          └─ Oui → d'où vient le temps ?
                   ├─ SQL (lectures, plan) → index, projection, sargabilité, split… (ch. 1 à 8) puis FromSql / TVF / vue
                   ├─ Transfert / matérialisation → projection, AsNoTracking, flux ; puis Dapper / ADO.NET
                   ├─ Pipeline EF (hot path mesuré) → requête compilée, pool ; puis Dapper
                   ├─ Écriture massive → ExecuteUpdate/Delete, SqlBulkCopy, procédure ensembliste
                   └─ Expressivité (fenêtrage, CTE, MERGE) → FromSql / linq2db / procédure
```

### Cinq questions avant de quitter LINQ

1. **Est-ce mesuré** (les trois axes) ou supposé ?
2. Le goulot est-il **le SQL ou l'ORM** ?
3. Peut-on le corriger **dans EF** (index, projection, split, compilation, `FromSql` partiel) ?
4. **Fréquence d'appel** × **gain par appel** = gain total : vaut-il la dépendance et la maintenance ?
5. Quel est le **coût de maintenance** (SQL en chaînes, perte du refactoring, tests d'intégration obligatoires) ?

### Tableau de synthèse

| Option | Gagne sur | Perd sur | Pour qui |
|---|---|---|---|
| EF LINQ | lisibilité, composition, refactoring | expressivité, surcoût du pipeline | 95 % des requêtes |
| `FromSql` / `SqlQuery` | expressivité sans quitter EF | typage, un peu de composition | cas isolés |
| Vue / TVF | logique réutilisable, composable | versionnement | requêtes nommées |
| Procédure | droits, multi-étapes, planification | composition, refactoring | traitements de fond |
| Dapper | simplicité, coût par ligne | SQL en chaînes | lectures critiques |
| linq2db | LINQ riche (fenêtrage, MERGE, bulk) | pas de graphe/migrations | si LINQ avancé requis |
| `SqlBulkCopy` | insertion massive | suivi, transactions manuelles | chargements |
| ADO.NET | surcoût minimal | tout est manuel | chemins extrêmes |
| CQRS / vue de lecture | lectures simples et rapides | duplication, cohérence | gros écrans de lecture |
| Moteur dédié | besoins hors OLTP | synchronisation, exploitation | après mesures |

### 🧪 TP 10.3 — « Le comité d'architecture »

*Énoncé.* On vous donne cinq cas (export mensuel de 20 M de lignes ; classement en temps réel ; import quotidien de 500 000 lignes ; recherche par mots dans 3 M de descriptions ; API de lecture par clé à 5 000 appels/s). Pour chacun, choisissez une technique, **justifiez par une mesure à faire** et donnez le plan B.

<details><summary>Correction</summary>

Export : flux ADO.NET/`bcp` ; classement : fenêtrage en SQL (index, vue indexée) ; import : `SqlBulkCopy` + `MERGE` ; recherche : plein texte, puis moteur dédié si facettes ; lecture par clé : EF compilé puis Dapper si le pipeline pèse. Chaque choix se justifie par **une mesure** (axe dominant, volume, fréquence) et prévoit son plan B si la mesure surprend.
</details>

---

## Script de nettoyage

```sql
DROP FUNCTION  IF EXISTS dbo.Lab_Top3Echanges;
DROP PROCEDURE IF EXISTS dbo.Lab_Top3Proc;
DROP PROCEDURE IF EXISTS dbo.Lab_BienParId;
DROP VIEW      IF EXISTS dbo.Lab_vwFichePlanete;
DROP TABLE     IF EXISTS dbo.Lab_ItemBulk;
DROP TABLE     IF EXISTS #moy;

-- Si vous avez créé des index de démonstration pour ces scénarios :
DROP INDEX IF EXISTS IX_Lab_Trades_Good_Price ON dbo.Trades;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
