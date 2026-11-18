# 01 — Opérateurs LINQ fondamentaux

> Suite de [`00-prerequis.md`](00-prerequis.md). **Tous les extraits utilisent la boîte à outils du 00** : `NewDb`, `Mesurer(...)`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plan d'exécution, `ToQueryString()`. Lisez d'abord sa section « Boîte à outils ».
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> Les index de démonstration portent le préfixe `IX_Lab_`, les procédures `Lab_` ; le **script de nettoyage** est en fin de fichier.

## Sommaire

- [1.1 Restriction et projection](#11-restriction-et-projection)
- [1.2 Tri et pagination](#12-tri-et-pagination)
- [1.3 Quantificateurs et opérateurs d'élément](#13-quantificateurs-et-opérateurs-délément)
- [1.4 Agrégation](#14-agrégation)
- [1.5 Regroupement (`GroupBy`)](#15-regroupement-groupby)
- [1.6 Jointures](#16-jointures)
- [1.7 Opérateurs ensemblistes](#17-opérateurs-ensemblistes)
- [1.8 Partitionnement, conversion, génération](#18-partitionnement-conversion-génération)
- [1.9 Nouveautés récentes du BCL](#19-nouveautés-récentes-du-bcl)
- [Script de nettoyage](#script-de-nettoyage)

Rappel des données utiles (`docs/01-mcd.md`) : `Goods` 1 M (`IsContraband` ≈ 8 %, `Sku` unique), `Inhabitants` 4 M (`IsActive` ≈ 92 %, `Gender` 0/1/2, `MidiChlorians` ≈ 75 % de `NULL`), `Planets` 3 M, `Markets` 2 M (`IsBlackMarket` ≈ 6 %), `Trades` 6 M (`Quantity` 1–500, `TradeType` 0/1, dates 2023-2025), `Companies` 1 M. **Seuls les index des clés étrangères et deux index uniques existent.**

---

## 1.1 Restriction et projection

**Axe concerné : 🗄️ SQL (colonnes et lignes lues, lookups) + 🧠 mémoire + ⚙️ .NET (matérialisation).**

### La notion

- `Where` filtre ; `Select` choisit **les colonnes** ; `SelectMany` aplatit une collection (→ `JOIN`/`APPLY`).
- Charger l'**entité complète** quand 2 colonnes suffisent coûte : plus de colonnes lues et transférées, plus d'objets, **suivi** (tracking) des entités, et un index *couvrant* devient impossible.
- Projeter vers un type anonyme, un `record`/DTO ou un tuple : EF ne lit **que** ce qui est utilisé, et ne suit pas le résultat.
- Un `Select` contenant une **sous-collection** (`p.Markets.Select(...)`) est traduit en une jointure (ou une sous-requête) : une seule requête, sans `Include`.
- La surcharge `Where((x, i) => …)` (avec l'index de l'élément) **n'est pas traduisible**.

### Dans quels cas on est concerné

- Listes, tableaux d'écrans, exports : on n'a besoin que de quelques colonnes.
- Lecture de données liées (parent + enfants) pour affichage.
- Boucles qui chargent des enfants un parent à la fois (**N+1**).

### Exemple sans l'amélioration

```csharp
// ❌ L'entité complète (12 colonnes, suivie) pour afficher deux champs
await Mesurer("AVANT : entité complète", async db =>
    (await db.Inhabitants.Where(i => i.PlanetId < 50_000).ToListAsync()).Count, afficherSql: true);

// ❌ Include : toutes les colonnes de TOUS les marchés + duplication des colonnes de la planète sur chaque ligne
await Mesurer("AVANT : Include", async db =>
    (await db.Planets.Include(p => p.Markets).Where(p => p.Id < 2_000).ToListAsync()).Count, afficherSql: true);

// ❌ N+1 : une requête pour les planètes, puis une par planète
await Mesurer("AVANT : N+1", async db =>
{
    var planetes = await db.Planets.AsNoTracking().Where(p => p.Id < 2_000).ToListAsync();
    var total = 0;
    foreach (var p in planetes)
        total += (await db.Markets.AsNoTracking().Where(m => m.PlanetId == p.Id).ToListAsync()).Count;   // 1 requête par planète
    return total;
});
```

**Pourquoi c'est mauvais** : le premier cas lit et alloue 12 colonnes × ~67 000 lignes pour en garder 2 ; le deuxième répète les colonnes de chaque planète sur chaque marché et suit toutes les entités ; le troisième multiplie les allers-retours (≈ 2 000 requêtes).

### Exemple avec l'amélioration

```csharp
// ✔ Projection : seulement les colonnes utiles, pas de suivi
await Mesurer("APRÈS : projection", async db =>
    (await db.Inhabitants.Where(i => i.PlanetId < 50_000)
        .Select(i => new { i.FirstName, i.LastName }).ToListAsync()).Count, afficherSql: true);

// ✔ Sous-collection dans le Select : une requête, uniquement les colonnes voulues
await Mesurer("APRÈS : Select + sous-collection", async db =>
    (await db.Planets.Where(p => p.Id < 2_000)
        .Select(p => new { p.Name, Marches = p.Markets.Select(m => m.Name).ToList() }).ToListAsync()).Count, afficherSql: true);

// ✔ SelectMany : une requête (jointure), résultat à plat
await Mesurer("APRÈS : SelectMany", async db =>
    (await db.Planets.Where(p => p.Id < 2_000)
        .SelectMany(p => p.Markets, (p, m) => new { Planete = p.Name, Marche = m.Name }).ToListAsync()).Count, afficherSql: true);
```

**Ce qu'il faut observer** : la liste des colonnes du `SELECT` (12 → 2), le nombre de requêtes (≈ 2 000 → 1), les allocations ; avec `Include`, notez le `LEFT JOIN` et la répétition des colonnes de `Planets`.

### Sans index / avec index

Le `WHERE PlanetId < 50000` utilise l'index de clé étrangère `IX_Inhabitants_PlanetId`, mais l'index ne contient **ni `FirstName` ni `LastName`** : pour chaque ligne trouvée, SQL Server fait un **Key Lookup** vers la table. Un index **couvrant** supprime ces lookups :

```sql
SET STATISTICS IO, TIME ON;
SELECT FirstName, LastName FROM dbo.Inhabitants WHERE PlanetId < 50000;     -- Index Seek + Key Lookup (voir le plan)
GO
CREATE INDEX IX_Lab_Inhabitants_Planet_Noms ON dbo.Inhabitants (PlanetId) INCLUDE (FirstName, LastName);
GO
SELECT FirstName, LastName FROM dbo.Inhabitants WHERE PlanetId < 50000;     -- Index Seek seul : plus de lookup
GO
DROP INDEX IX_Lab_Inhabitants_Planet_Noms ON dbo.Inhabitants;
```
Relancez le cas C# « projection » avant/après. **Remarque** : la **projection est la condition** pour qu'un index couvrant serve ; avec l'entité complète (12 colonnes), il faudrait tout inclure.

### Sans objet : procédure stockée

Une procédure ne change rien ici : c'est le *choix des colonnes* qui compte, pas le véhicule.

### 🧪 TP 1.1 — « Le tableau des planètes »

*Énoncé.* Un écran doit lister, pour les planètes d'`Id` < 2 000, le nom de la planète et la **liste des noms de ses marchés noirs** (`IsBlackMarket`). Écrivez-le (a) avec `Include` + filtre en mémoire, (b) avec une projection en une seule requête. Comparez le SQL, le nombre de lignes lues et les allocations.

<details><summary>Correction</summary>

```csharp
// (a) Include puis filtre en mémoire : tous les marchés sont rapatriés, noirs ou non
await Mesurer("TP (a)", async db =>
{
    var l = await db.Planets.AsNoTracking().Include(p => p.Markets).Where(p => p.Id < 2_000).ToListAsync();
    return l.Select(p => new { p.Name, Noirs = p.Markets.Where(m => m.IsBlackMarket).Select(m => m.Name).ToList() }).Count();
}, afficherSql: true);

// (b) Projection : le filtre des marchés noirs est dans le SQL, seuls les noms reviennent
await Mesurer("TP (b)", async db =>
    (await db.Planets.Where(p => p.Id < 2_000)
        .Select(p => new { p.Name, Noirs = p.Markets.Where(m => m.IsBlackMarket).Select(m => m.Name).ToList() })
        .ToListAsync()).Count, afficherSql: true);
```
(b) lit moins de colonnes et moins de lignes (≈ 6 % des marchés), n'a pas de suivi. Vérifiez avec `ToQueryString()` que le `IsBlackMarket = 1` est bien **dans** la requête.
</details>

---

## 1.2 Tri et pagination

**Axe concerné : 🗄️ SQL (tri, lignes parcourues puis ignorées) + justesse des pages.**

### La notion

- `OrderBy`/`ThenBy` → `ORDER BY` ; sans `ORDER BY`, SQL Server ne **garantit aucun ordre**.
- `Skip(n).Take(m)` → `OFFSET n ROWS FETCH NEXT m ROWS ONLY` : le serveur **lit puis jette** les `n` premières lignes. Le coût **croît avec la profondeur** de la page.
- **Pagination par clé (keyset / seek)** : `Where(clé > dernièreClé)` + `OrderBy(clé)` + `Take` ; coût constant à toute profondeur, à condition d'avoir un index sur la clé de tri.
- **Ordre déterministe** : si le critère de tri n'est pas unique, ajoutez une clé unique en dernier critère (`ThenBy(x => x.Id)`), sinon deux pages peuvent contenir la même ligne, ou en manquer.

### Dans quels cas on est concerné

- Listes paginées (grilles, API `?page=`), exports par pages, défilement infini.
- Tout `Take`/`Skip` sans `OrderBy` : EF journalise un avertissement.
- Un `ORDER BY` sur une colonne **sans index** : tri de tout le jeu de résultats (ou tri *Top N*).

### Exemple sans l'amélioration

```csharp
// ❌ Page très profonde en OFFSET : 3 M de lignes lues puis ignorées
await Mesurer("AVANT : Skip(3 000 000)", async db =>
    (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Skip(3_000_000).Take(50).ToListAsync()).Count, afficherSql: true);

// ❌ Les 50 derniers échanges (tri sur une colonne non indexée)
await Mesurer("AVANT : ORDER BY TradedAt DESC sans index", async db =>
    (await db.Trades.AsNoTracking().OrderByDescending(t => t.TradedAt).Take(50).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Keyset : on mémorise la dernière clé de la page précédente
await Mesurer("APRÈS : keyset (Id > 3 000 000)", async db =>
    (await db.Inhabitants.AsNoTracking().Where(i => i.Id > 3_000_000).OrderBy(i => i.Id).Take(50).ToListAsync()).Count, afficherSql: true);

```

**Keyset sur une clé composée** (à comprendre, EF n'a pas de comparaison de tuples) :

```csharp
// page suivante après (dernierTradedAt, dernierId) pour ORDER BY TradedAt DESC, Id DESC
db.Trades.Where(t => t.TradedAt < dernierTradedAt || (t.TradedAt == dernierTradedAt && t.Id < dernierId))
         .OrderByDescending(t => t.TradedAt).ThenByDescending(t => t.Id).Take(50);
```

**Ce qu'il faut observer** : dans le plan de `Skip(3 000 000)`, le nombre de lignes lues par l'index clustered (≈ 3 M) contre quelques dizaines pour le keyset ; *logical reads* qui **augmentent avec la profondeur** de l'offset ; le `Top N Sort` du tri non indexé.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
SELECT TOP (50) * FROM dbo.Trades ORDER BY TradedAt DESC;      -- sans index : Clustered Index Scan (6 M) + Top N Sort
GO
CREATE INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades (TradedAt, Id);
GO
SELECT TOP (50) * FROM dbo.Trades ORDER BY TradedAt DESC;      -- Index Scan arrière limité à 50 lignes + 50 Key Lookups, pas de Sort
GO
DROP INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades;
```
Relancez le cas C# « ORDER BY TradedAt DESC » avant/après. *Lecture* : l'index fournit les lignes **déjà triées** ; SQL Server s'arrête à 50.

### Comparaison avec une procédure stockée (page par clé)

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_PageHabitants @Apres int, @Taille int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Taille) Id, PlanetId, SpeciesId, FactionId, FirstName, LastName, BirthYear, Gender, Credits, MidiChlorians, IsActive
    FROM dbo.Inhabitants WHERE Id > @Apres ORDER BY Id;
END
```
```csharp
await Mesurer("PROC : page par clé", async db =>
    (await db.Inhabitants.FromSql($"EXEC dbo.Lab_PageHabitants {3_000_000}, {50}").AsNoTracking().ToListAsync()).Count);
```
**À observer** : même plan (Clustered Index Seek) et mêmes lectures que le LINQ `Where(i => i.Id > …).Take(50)`. La procédure ne fait pas mieux ; elle fige simplement la requête.

### 🧪 TP 1.2 — « Parcourir tout `Inhabitants` sans exploser »

*Énoncé.* Écrivez un parcours de l'ensemble de la table par pages de 10 000 : (a) en `Skip/Take` avec `OrderBy(Id)`, (b) en keyset. Limitez-vous aux 200 premières pages. Mesurez le temps **de chacune des 5 dernières pages** dans les deux cas et expliquez la tendance.

<details><summary>Correction</summary>

```csharp
await Mesurer("TP (a) Skip/Take", async db =>
{
    var chrono = new List<long>();
    for (var page = 0; page < 200; page++)
    {
        var t0 = Stopwatch.GetTimestamp();
        await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Skip(page * 10_000).Take(10_000).Select(i => i.Id).ToListAsync();
        chrono.Add((long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    return "5 dernières pages (ms) : " + string.Join(", ", chrono.TakeLast(5));
});

await Mesurer("TP (b) keyset", async db =>
{
    var chrono = new List<long>(); var dernier = 0;
    for (var page = 0; page < 200; page++)
    {
        var t0 = Stopwatch.GetTimestamp();
        var ids = await db.Inhabitants.AsNoTracking().Where(i => i.Id > dernier).OrderBy(i => i.Id).Take(10_000).Select(i => i.Id).ToListAsync();
        dernier = ids[^1];
        chrono.Add((long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    return "5 dernières pages (ms) : " + string.Join(", ", chrono.TakeLast(5));
});
```
Tendance attendue : (a) **ralentit linéairement** avec la page (le serveur relit et jette tout le début) ; (b) **reste constant**. Limite du keyset : on ne peut pas « sauter à la page 137 » ; il convient à un parcours séquentiel (suivant/précédent, export).
</details>

---

## 1.3 Quantificateurs et opérateurs d'élément

**Axe concerné : 🗄️ SQL (arrêt anticipé vs parcours complet).**

### La notion

| Opérateur | SQL | À savoir |
|---|---|---|
| `Any(pred)` | `EXISTS(...)` | s'arrête dès **une** ligne trouvée |
| `Count(pred) > 0` | `COUNT(*)` | **parcourt tout** ce qui correspond |
| `All(pred)` | `NOT EXISTS(… NOT pred)` | s'arrête à la 1ʳᵉ ligne qui viole |
| `Contains(x)` sur liste locale | `IN (…)` | dépend de la taille de la liste (voir ch. 2) |
| `First/FirstOrDefault` | `TOP(1)` | exige un ordre déterministe pour un résultat stable |
| `Single/SingleOrDefault` | `TOP(2)` | lève une exception si 0 ou ≥ 2 lignes |
| `Last` | `ORDER BY … DESC TOP(1)` | **exige un `OrderBy`** |
| `DefaultIfEmpty()` | brique du `LEFT JOIN` | |

### Dans quels cas on est concerné

- Tests d'existence (`if (q.Count() > 0)`), « y a-t-il des … ? ».
- Recherche d'un élément unique par clé non primaire (`Single` vs `First`).
- Recherche des éléments **sans** correspondance (anti-jointure) : `Any()` négatif contre comparaison en mémoire.

### Exemple sans l'amélioration

```csharp
// ❌ Count() > 0 : compte TOUTES les lignes qui correspondent
await Mesurer("AVANT : Count() > 0", async db =>
    await db.Trades.CountAsync(t => t.Quantity == 500) > 0, afficherSql: true);

// ❌ Anti-jointure en mémoire : planètes sans marché
await Mesurer("AVANT : Except en mémoire", async db =>
{
    var toutes   = await db.Planets.AsNoTracking().Where(p => p.Id < 300_000).Select(p => p.Id).ToListAsync();
    var avecMarche = await db.Markets.AsNoTracking().Where(m => m.PlanetId < 300_000).Select(m => m.PlanetId).Distinct().ToListAsync();
    return toutes.Except(avecMarche).Count();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Any : EXISTS, s'arrête à la première ligne
await Mesurer("APRÈS : Any", db => db.Trades.AnyAsync(t => t.Quantity == 500), afficherSql: true);

// ✔ Anti-jointure en SQL : NOT EXISTS
await Mesurer("APRÈS : NOT EXISTS", db =>
    db.Planets.Where(p => p.Id < 300_000 && !p.Markets.Any()).CountAsync(), afficherSql: true);
```

**Comportements à connaître** (à essayer, le résumé suffit) :

```csharp
await Mesurer("First vs Single vs Last", async db =>
{
    var sku = (await db.Goods.AsNoTracking().OrderBy(g => g.Id).FirstAsync()).Sku;
    var f = await db.Goods.AsNoTracking().FirstAsync(g => g.Sku == sku);     // TOP(1)
    var s = await db.Goods.AsNoTracking().SingleAsync(g => g.Sku == sku);    // TOP(2) : sert à détecter les doublons
    var l = await db.Goods.AsNoTracking().OrderBy(g => g.Id).LastAsync(g => g.Category == 3);  // sans OrderBy : exception
    try { await db.Goods.AsNoTracking().SingleAsync(g => g.IsContraband); } catch (Exception e) { return $"Single sur plusieurs lignes : {e.GetType().Name}"; }
    return "?";
}, afficherSql: true);
```

**Ce qu'il faut observer** : `Any` → `SELECT CASE WHEN EXISTS (…) THEN 1 ELSE 0`, *logical reads* très inférieurs à `COUNT(*)` quand beaucoup de lignes correspondent ; `Single` → `TOP(2)` dans le SQL.

### Sans index / avec index

Le `Any` s'arrête au premier résultat : s'il y en a **beaucoup**, il est rapide même sans index. S'il n'y en a **aucun** (ou très peu), SQL doit tout parcourir sans index :

```sql
SET STATISTICS IO ON;
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Trades WHERE Quantity = 500) THEN 1 ELSE 0 END;     -- trouve vite
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Trades WHERE Quantity = 100000) THEN 1 ELSE 0 END;  -- aucune ligne : scan complet
GO
CREATE INDEX IX_Lab_Trades_Qty ON dbo.Trades (Quantity);
GO
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Trades WHERE Quantity = 100000) THEN 1 ELSE 0 END;  -- Seek : quelques lectures
GO
DROP INDEX IX_Lab_Trades_Qty ON dbo.Trades;
```
*Lecture* : le « mauvais cas » de `Any` est l'**absence** de résultat ; l'index le résout. Pour `Count > 0`, l'index n'évite pas de compter toutes les lignes correspondantes.

### Comparaison avec une procédure stockée

Non pertinent : `EXISTS` est la même instruction dans LINQ et dans une procédure. Utile seulement à titre d'exercice : écrire `IF EXISTS (…)` dans une procédure retournant un bit et constater **le même plan**.

### 🧪 TP 1.3 — « Existe-t-il… ? »

*Énoncé.* Écrivez une méthode `ExisteGrosEchange(db, qte)` qui répond « existe-t-il un échange de quantité ≥ `qte` ? ». Écrivez-la avec `Count() > 0` puis avec `Any`, et mesurez pour `qte = 500` (nombreux résultats) et `qte = 501` (aucun). Pour quel cas l'écart est-il le plus grand, et pourquoi ? Que change un index sur `Quantity` ?

<details><summary>Correction</summary>

```csharp
static async Task<bool> V1(StarWarsContext db, int q) => await db.Trades.CountAsync(t => t.Quantity >= q) > 0;
static Task<bool>       V2(StarWarsContext db, int q) => db.Trades.AnyAsync(t => t.Quantity >= q);

foreach (var q in new[] { 500, 501 })
{
    await Mesurer($"Count>0  qte={q}", db => V1(db, q));
    await Mesurer($"Any      qte={q}", db => V2(db, q));
}
```
- `qte = 500` : `Any` s'arrête à la première ligne trouvée (très peu de lectures) ; `Count` lit tout.
- `qte = 501` : aucune ligne → `Any` parcourt tout, comme `Count` : les deux coûtent un scan complet, **sauf avec un index sur `Quantity`**, qui ramène les deux à quelques lectures (le `Count` d'un résultat vide devient un seek). Conclusion : toujours `Any` pour une existence ; l'index traite le cas « absent ».
</details>

---

## 1.4 Agrégation

**Axe concerné : 🗄️ SQL (une seule passe) + 🧠 mémoire + ⚙️ .NET.**

### La notion

`Count`, `LongCount`, `Sum`, `Average`, `Min`, `Max` sont traduits en agrégats SQL : **une seule ligne** revient, quelle que soit la taille de la table. `Aggregate` ne l'est pas. Les agrégats portent des **pièges de sémantique** : ensemble vide (`Max`/`Min`/`Average` sur type non nullable : exception ; cast vers un type nullable pour obtenir `null`), types étroits (`int` qui déborde, voir 0.7).

### Dans quels cas on est concerné

- Tableaux de bord, totaux, statistiques.
- Plusieurs agrégats indépendants sur la même table (5 requêtes au lieu d'une).
- Calculs « maison » avec `Aggregate`, ou `ToList()` suivi de `Sum` en mémoire.

### Exemple sans l'amélioration

```csharp
// ❌ Aggregate n'est pas traduisible : il faut tout rapatrier (1 M de biens)
await Mesurer("AVANT : Aggregate en mémoire", db =>
    Task.FromResult(db.Goods.AsNoTracking().AsEnumerable().Aggregate(0m, (acc, g) => acc + g.BasePrice)));

// ❌ Cinq requêtes pour cinq chiffres
await Mesurer("AVANT : 5 agrégats = 5 requêtes", async db =>
{
    var n   = await db.Trades.CountAsync();
    var sq  = await db.Trades.SumAsync(t => (long)t.Quantity);
    var mn  = await db.Trades.MinAsync(t => t.UnitPrice);
    var mx  = await db.Trades.MaxAsync(t => t.UnitPrice);
    var avg = await db.Trades.AverageAsync(t => t.UnitPrice);
    return $"n={n} Σq={sq} min={mn} max={mx} moy={avg:N2}";
}, afficherSql: true);

// ❌ Max sur un ensemble vide : exception
await Mesurer("AVANT : Max sur vide", async db =>
{
    try { return (await db.Goods.Where(g => g.Id < 0).MaxAsync(g => g.BasePrice)).ToString(); }
    catch (Exception e) { return e.GetType().Name; }
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Sum traduit en SQL : une ligne revient
await Mesurer("APRÈS : SumAsync", async db => await db.Goods.SumAsync(g => g.BasePrice));

// ✔ Un seul passage : GroupBy sur une constante
await Mesurer("APRÈS : 1 requête, 5 agrégats", async db =>
{
    var r = await db.Trades.GroupBy(_ => 1).Select(g => new
    {
        N = g.Count(), SommeQte = g.Sum(t => (long)t.Quantity),
        Min = g.Min(t => t.UnitPrice), Max = g.Max(t => t.UnitPrice), Moy = g.Average(t => t.UnitPrice)
    }).SingleAsync();
    return $"n={r.N} Σq={r.SommeQte} min={r.Min} max={r.Max} moy={r.Moy:N2}";
}, afficherSql: true);

// ✔ Ensemble vide : cast nullable pour obtenir null au lieu d'une exception
await Mesurer("APRÈS : Max nullable", async db =>
    (await db.Goods.Where(g => g.Id < 0).MaxAsync(g => (decimal?)g.BasePrice))?.ToString() ?? "null");
```

**Ce qu'il faut observer** : 5 requêtes → 1 (`SELECT COUNT(*), SUM(...), MIN(...), MAX(...), AVG(...)`) ; *logical reads* divisés par ~5 (5 scans → 1).

### Sans index / avec index

`Min`/`Max` d'une colonne **indexée** se lisent aux extrémités de l'index :

```sql
SET STATISTICS IO, TIME ON;
SELECT MAX(BasePrice) FROM dbo.Goods;                           -- sans index : scan de 1 M de lignes
GO
CREATE INDEX IX_Lab_Goods_BasePrice ON dbo.Goods (BasePrice);
GO
SELECT MAX(BasePrice) FROM dbo.Goods;                           -- Index Seek (dernière page) : ~3 lectures
GO
DROP INDEX IX_Lab_Goods_BasePrice ON dbo.Goods;
```
Les agrégats **sur toute la table** (Sum/Count/Average) bénéficient d'un index **étroit** (moins de pages à lire) :

```sql
CREATE INDEX IX_Lab_Trades_Agg ON dbo.Trades (UnitPrice) INCLUDE (Quantity);
-- relancer le cas C# « 1 requête, 5 agrégats » : le plan lit l'index étroit au lieu de la table complète
DROP INDEX IX_Lab_Trades_Agg ON dbo.Trades;
```

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_ResumeTrades
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT_BIG(*) AS N, SUM(CAST(Quantity AS bigint)) AS SommeQte,
           MIN(UnitPrice) AS Min, MAX(UnitPrice) AS Max, AVG(UnitPrice) AS Moy
    FROM dbo.Trades;
END
```
```csharp
public record ResumeTrades(long N, long SommeQte, decimal Min, decimal Max, decimal Moy);

await Mesurer("PROC : Lab_ResumeTrades", async db =>
{
    var r = (await db.Database.SqlQuery<ResumeTrades>($"EXEC dbo.Lab_ResumeTrades").ToListAsync()).Single();
    return $"n={r.N} Σq={r.SommeQte} min={r.Min} max={r.Max} moy={r.Moy:N2}";
});
```
**À observer** : même plan que le `GroupBy(_ => 1)` LINQ, mêmes *logical reads*. Remarque : `COUNT(*)` renvoie un `int`, `COUNT_BIG(*)` un `bigint` — la correspondance de type avec le `record` compte.

### 🧪 TP 1.4 — « Le tableau de bord en un aller-retour »

*Énoncé.* Un tableau de bord affiche : nombre d'habitants, nombre d'actifs, crédits totaux, crédits maximum, âge moyen supposé (`2026 - BirthYear`). Écrivez-le (a) en 5 requêtes, (b) en une requête. Quelle différence sur les *logical reads* ? Que se passe-t-il si `IsActive` est ajouté comme filtre d'un des agrégats ?

<details><summary>Correction</summary>

```csharp
await Mesurer("TP (a) 5 requêtes", async db =>
{
    var n  = await db.Inhabitants.CountAsync();
    var a  = await db.Inhabitants.CountAsync(i => i.IsActive);
    var s  = await db.Inhabitants.SumAsync(i => i.Credits);
    var mx = await db.Inhabitants.MaxAsync(i => i.Credits);
    var ag = await db.Inhabitants.AverageAsync(i => 2026 - i.BirthYear);
    return $"{n} {a} {s:N0} {mx} {ag:N1}";
}, afficherSql: true);

await Mesurer("TP (b) 1 requête", async db =>
{
    var r = await db.Inhabitants.GroupBy(_ => 1).Select(g => new
    {
        N = g.Count(), Actifs = g.Count(i => i.IsActive),                  // filtre dans l'agrégat : COUNT(CASE WHEN …)
        Credits = g.Sum(i => i.Credits), Max = g.Max(i => i.Credits), Age = g.Average(i => 2026 - i.BirthYear)
    }).SingleAsync();
    return $"{r.N} {r.Actifs} {r.Credits:N0} {r.Max} {r.Age:N1}";
}, afficherSql: true);
```
(b) lit la table une fois (≈ 1/5 des lectures de (a)). Le `Count(i => i.IsActive)` dans un `GroupBy` devient un `COUNT` conditionnel (`CASE WHEN`) : toujours **une seule passe**, contrairement à un `Where` appliqué avant l'agrégation, qui filtrerait tous les agrégats.
</details>

---

## 1.5 Regroupement (`GroupBy`)

**Axe concerné : 🗄️ SQL + 🧠 mémoire + ⚙️ .NET.**

### La notion

`GroupBy` suivi d'une **projection d'agrégats** (`g.Key`, `g.Count()`, `g.Sum(...)`, `g.Average(...)`) est traduit en `GROUP BY` : seuls les groupes reviennent. En LINQ to Objects, `GroupBy` **matérialise** les groupes avec tous leurs éléments. Un `Where` placé **après** le `GroupBy` sur un agrégat équivaut à `HAVING`. La **traduction des éléments** d'un groupe (`g.OrderBy(...).First()`) dépend de la version d'EF : vérifiez avec `ToQueryString()`.

### Dans quels cas on est concerné

- Statistiques par catégorie, tableaux croisés, « top N par groupe ».
- Code qui fait `ToList()` puis `GroupBy` en mémoire.
- Filtre sur un agrégat (`HAVING`).

### Exemple sans l'amélioration

```csharp
// ❌ 4 M de lignes ramenées pour calculer 3 groupes
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Inhabitants.AsNoTracking().Select(i => new { i.Gender, i.Credits }).ToListAsync();
    var g = l.GroupBy(x => x.Gender).Select(x => new { x.Key, N = x.Count(), Moy = x.Average(y => y.Credits) }).ToList();
    return string.Join(" | ", g.Select(x => $"{x.Key}: {x.N} / {x.Moy:N0}"));
});

// ❌ Filtre sur l'agrégat fait en mémoire : tous les biens, tous les échanges
await Mesurer("AVANT : HAVING en mémoire", async db =>
{
    var l = await db.Trades.AsNoTracking().Select(t => t.GoodId).ToListAsync();
    return l.GroupBy(x => x).Count(g => g.Count() > 12);
});
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : GroupBy SQL", async db =>
{
    var g = await db.Inhabitants.GroupBy(i => i.Gender)
        .Select(x => new { x.Key, N = x.Count(), Moy = x.Average(y => y.Credits) }).ToListAsync();
    return string.Join(" | ", g.Select(x => $"{x.Key}: {x.N} / {x.Moy:N0}"));
}, afficherSql: true);

// ✔ HAVING : Where après le GroupBy
await Mesurer("APRÈS : HAVING SQL", db =>
    db.Trades.GroupBy(t => t.GoodId).Where(g => g.Count() > 12).CountAsync(), afficherSql: true);

// ? Éléments d'un groupe : l'échange le plus cher par société, parmi les 1 000 premières sociétés
await Mesurer("Plus gros échange par société", async db =>
{
    var q = db.Trades.Where(t => t.CompanyId <= 1_000)
        .GroupBy(t => t.CompanyId)
        .Select(g => new { g.Key, Max = g.OrderByDescending(t => t.UnitPrice).First().UnitPrice });
    Console.WriteLine(q.ToQueryString());                  // vérifier la traduction avant d'exécuter
    return (await q.ToListAsync()).Count;
});
```

**Ce qu'il faut observer** : le `GROUP BY` / `HAVING` dans le SQL ; lignes lues (4 M / 6 M → quelques lignes) ; pour le dernier cas, le plan (`Sort`, `Stream Aggregate`, ou `ROW_NUMBER` selon la traduction).

### Sans index / avec index

Un `GROUP BY` sur une colonne **indexée** peut utiliser un `Stream Aggregate` sur un index déjà trié (sans table de hachage ni tri) :

```sql
SET STATISTICS IO, TIME ON;
SELECT Quantity, COUNT(*) FROM dbo.Trades GROUP BY Quantity;     -- sans index : Hash Match Aggregate sur le clustered
GO
CREATE INDEX IX_Lab_Trades_Qty ON dbo.Trades (Quantity);
GO
SELECT Quantity, COUNT(*) FROM dbo.Trades GROUP BY Quantity;     -- Stream Aggregate sur l'index étroit (trié)
GO
DROP INDEX IX_Lab_Trades_Qty ON dbo.Trades;
```
Même idée pour `GROUP BY GoodId` : l'index `IX_Trades_GoodId` (clé étrangère) existe déjà, d'où un plan différent de celui de `GROUP BY Quantity`. Comparez les deux plans.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_BiensPopulaires @Min int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT GoodId, COUNT(*) AS Echanges FROM dbo.Trades GROUP BY GoodId HAVING COUNT(*) > @Min;
END
```
```csharp
public record BienPopulaire(int GoodId, int Echanges);
await Mesurer("PROC : biens populaires", async db =>
    (await db.Database.SqlQuery<BienPopulaire>($"EXEC dbo.Lab_BiensPopulaires {12}").ToListAsync()).Count);
```
**À observer** : même `GROUP BY … HAVING`, même plan que le LINQ ; seul l'objet de base change.

### 🧪 TP 1.5 — « Statistiques par genre et par état »

*Énoncé.* Pour `Inhabitants`, calculez par `(Gender, IsActive)` : nombre, crédit moyen, crédit maximum. (a) en mémoire, (b) en SQL. Dans (b), retirez les groupes de moins de 100 000 habitants avec un `HAVING`. Montrez le SQL.

<details><summary>Correction</summary>

```csharp
await Mesurer("TP (b)", async db =>
{
    var r = await db.Inhabitants
        .GroupBy(i => new { i.Gender, i.IsActive })                       // clé composite
        .Select(g => new { g.Key.Gender, g.Key.IsActive, N = g.Count(), Moy = g.Average(i => i.Credits), Max = g.Max(i => i.Credits) })
        .Where(x => x.N >= 100_000)                                         // HAVING COUNT(*) >= 100000
        .OrderBy(x => x.Gender).ThenBy(x => x.IsActive).ToListAsync();
    return string.Join(" | ", r.Select(x => $"{x.Gender}/{x.IsActive}:{x.N}"));
}, afficherSql: true);
```
La clé composite devient `GROUP BY [Gender], [IsActive]` ; le `Where` sur `N` se traduit en `HAVING`. Le résultat tient en six lignes au plus, contre 4 M de lignes à transférer pour la version mémoire.
</details>

---

## 1.6 Jointures

**Axe concerné : 🗄️ SQL (algorithme de jointure, lookups) + ⚙️ .NET + allers-retours.**

### La notion

| Besoin | Écriture LINQ | SQL |
|---|---|---|
| Jointure interne | `Join(...)` ou navigation (`m.Planet.Name`) | `INNER JOIN` |
| Jointure externe gauche | `GroupJoin` + `SelectMany` + `DefaultIfEmpty` — ou l'opérateur natif **`LeftJoin`** (.NET 10, traduit par EF 10/11) | `LEFT JOIN` |
| Données liées | `Include` / `Select` avec navigations | jointure(s) |
| Jointure croisée | `SelectMany` sans condition | `CROSS JOIN` |

Écrire la jointure **dans la base** permet à l'optimiseur de choisir l'algorithme (*Nested Loops*, *Hash Match*, *Merge Join*) selon les index et les volumes. Joindre **deux listes en mémoire** force un algorithme .NET et impose de ramener les deux jeux.

### Dans quels cas on est concerné

- Boucle qui charge des données liées (N+1) ; jointure faite en mémoire entre deux `ToList()`.
- Outer join écrit « à la main » (`GroupJoin`…) quand `LeftJoin` est disponible.
- Choix entre `Include`, `Select` et `Join` explicite.

### Exemple sans l'amélioration

```csharp
// ❌ Jointure en mémoire : on rapatrie 300 000 planètes pour en utiliser quelques milliers
await Mesurer("AVANT : join en mémoire", async db =>
{
    var marches  = await db.Markets.AsNoTracking().Where(m => m.IsBlackMarket && m.PlanetId < 300_000).ToListAsync();
    var planetes = await db.Planets.AsNoTracking().Where(p => p.Id < 300_000).ToListAsync();
    return marches.Join(planetes, m => m.PlanetId, p => p.Id, (m, p) => new { p.Name, Marche = m.Name }).Count();
});

// ❌ N+1 : une requête de planète par marché
await Mesurer("AVANT : N+1", async db =>
{
    var marches = await db.Markets.AsNoTracking().Where(m => m.IsBlackMarket && m.PlanetId < 50_000).ToListAsync();
    var n = 0;
    foreach (var m in marches) n += (await db.Planets.AsNoTracking().FirstAsync(p => p.Id == m.PlanetId)).Name.Length;
    return n;
});

// ❌ Left join à l'ancienne (verbeux, source d'erreurs)
await Mesurer("AVANT : GroupJoin + DefaultIfEmpty", async db =>
    (await db.Planets.Where(p => p.Id < 300_000)
        .GroupJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, ms) => new { p, ms })
        .SelectMany(x => x.ms.DefaultIfEmpty(), (x, m) => new { x.p.Name, Marche = m == null ? null : m.Name })
        .ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Jointure par navigation : une requête, SQL choisit l'algorithme
await Mesurer("APRÈS : navigation", async db =>
    (await db.Markets.Where(m => m.IsBlackMarket && m.PlanetId < 300_000)
        .Select(m => new { m.Planet.Name, Marche = m.Name }).ToListAsync()).Count, afficherSql: true);

// ✔ Join explicite
await Mesurer("APRÈS : Join", async db =>
    (await db.Markets.Where(m => m.IsBlackMarket && m.PlanetId < 300_000)
        .Join(db.Planets, m => m.PlanetId, p => p.Id, (m, p) => new { p.Name, Marche = m.Name }).ToListAsync()).Count, afficherSql: true);

// ✔ LeftJoin natif (si disponible dans votre SDK .NET / EF Core)
await Mesurer("APRÈS : LeftJoin natif", async db =>
    (await db.Planets.Where(p => p.Id < 300_000)
        .LeftJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, m) => new { p.Name, Marche = m == null ? null : m.Name })
        .ToListAsync()).Count, afficherSql: true);
```

**Ce qu'il faut observer** : le nombre de requêtes (N+1 → 1) ; le SQL de `GroupJoin+DefaultIfEmpty` et de `LeftJoin` (tous deux `LEFT JOIN`, comparez-les) ; dans le plan, le **type de jointure** retenu et le côté « build » d'un *Hash Match*.

### Sans index / avec index

La jointure `Markets.PlanetId = Planets.Id` s'appuie sur l'index de clé étrangère `IX_Markets_PlanetId`. Pour voir un plan **changer**, filtrez sur une colonne de `Planets` non indexée :

```sql
SET STATISTICS IO, TIME ON;
SELECT m.Id, p.Name FROM dbo.Markets m JOIN dbo.Planets p ON p.Id = m.PlanetId
WHERE p.Population > 99000000000;                               -- filtre non indexé : scan de Planets puis jointure
GO
CREATE INDEX IX_Lab_Planets_Pop_Cover ON dbo.Planets (Population) INCLUDE (Name);
GO
SELECT m.Id, p.Name FROM dbo.Markets m JOIN dbo.Planets p ON p.Id = m.PlanetId
WHERE p.Population > 99000000000;                               -- Seek sur Planets puis Nested Loops via IX_Markets_PlanetId
GO
DROP INDEX IX_Lab_Planets_Pop_Cover ON dbo.Planets;
```
Écrivez le même filtre en LINQ (`Where(m => m.Planet.Population > 99_000_000_000)`) et comparez avant/après index.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_PlanetesMarchesNoirs @PlanetMax int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.Name AS Planete, m.Name AS Marche
    FROM dbo.Markets m JOIN dbo.Planets p ON p.Id = m.PlanetId
    WHERE m.IsBlackMarket = 1 AND m.PlanetId < @PlanetMax;
END
```
```csharp
public record PlaneteMarche(string Planete, string Marche);
await Mesurer("PROC : planètes / marchés noirs", async db =>
    (await db.Database.SqlQuery<PlaneteMarche>($"EXEC dbo.Lab_PlanetesMarchesNoirs {300_000}").ToListAsync()).Count);
```
**À observer** : même plan que le LINQ par navigation. Ce que la procédure n'offre pas : composer ensuite un `Where`/`OrderBy` côté C# **dans le même SQL**.

### 🧪 TP 1.6 — « Planètes sans marché »

*Énoncé.* Listez, pour les planètes d'`Id` < 300 000, le nom de chaque planète et le nombre de ses marchés, **y compris celles qui n'en ont aucun**. Écrivez (a) le `GroupJoin` classique, (b) `LeftJoin`/`Count` sur navigation, (c) la forme la plus simple (`Select` avec `p.Markets.Count()`). Comparez les SQL.

<details><summary>Correction</summary>

```csharp
// (c) la plus simple : sous-requête corrélée COUNT
await Mesurer("TP (c)", async db =>
    (await db.Planets.Where(p => p.Id < 300_000).Select(p => new { p.Name, Marches = p.Markets.Count() }).ToListAsync()).Count, afficherSql: true);

// (a) GroupJoin : GROUP/COUNT après LEFT JOIN
await Mesurer("TP (a)", async db =>
    (await db.Planets.Where(p => p.Id < 300_000)
        .GroupJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, ms) => new { p.Name, Marches = ms.Count() }).ToListAsync()).Count, afficherSql: true);
```
Les trois écritures renvoient le même résultat (les planètes sans marché apparaissent avec `0`) ; comparez les plans : sous-requête corrélée (*Nested Loops* + agrégat) ou `LEFT JOIN` + `GROUP BY` ; l'optimiseur peut aboutir au même plan physique. C'est l'occasion de constater que **l'écriture LINQ n'impose pas le plan**.
</details>

---

## 1.7 Opérateurs ensemblistes

**Axe concerné : 🗄️ SQL (tri/hachage pour dédoublonner) + 🧠 mémoire.**

### La notion

| LINQ | SQL | Coût |
|---|---|---|
| `Distinct` | `SELECT DISTINCT` | dédoublonnage (hachage ou tri) |
| `Union` | `UNION` | **dédoublonne** : tri/hachage de l'ensemble |
| `Concat` | `UNION ALL` | simple concaténation |
| `Intersect` / `Except` | `INTERSECT` / `EXCEPT` | dédoublonnent aussi |
| `*By` (`DistinctBy`, `UnionBy`, `ExceptBy`, `MinBy`/`MaxBy`) | support de traduction **limité** : vérifier | |

Règle : `Concat` si les doublons sont impossibles ou acceptables ; `Union` seulement si le dédoublonnage est voulu.

### Dans quels cas on est concerné

- Fusion de deux sources (`Union`) alors qu'elles sont disjointes.
- `Distinct` pour contourner un produit cartésien involontaire.
- « Éléments de A qui ne sont pas dans B » fait en mémoire.

### Exemple sans l'amélioration

```csharp
// ❌ Union quand les deux ensembles sont disjoints : dédoublonnage inutile
await Mesurer("AVANT : Union", async db =>
{
    var a = db.Trades.Where(t => t.TradeType == 0 && t.GoodId < 20_000).Select(t => t.Id);
    var b = db.Trades.Where(t => t.TradeType == 1 && t.GoodId < 20_000).Select(t => t.Id);
    return await a.Union(b).CountAsync();
}, afficherSql: true);

// ❌ Distinct sur 6 M de valeurs pour savoir quels biens ont été échangés
await Mesurer("AVANT : Distinct", async db => (await db.Trades.Select(t => t.GoodId).Distinct().ToListAsync()).Count);

// ❌ Except en mémoire (déjà vu en 1.3)
```

### Exemple avec l'amélioration

```csharp
// ✔ Concat : UNION ALL
await Mesurer("APRÈS : Concat", async db =>
{
    var a = db.Trades.Where(t => t.TradeType == 0 && t.GoodId < 20_000).Select(t => t.Id);
    var b = db.Trades.Where(t => t.TradeType == 1 && t.GoodId < 20_000).Select(t => t.Id);
    return await a.Concat(b).CountAsync();
}, afficherSql: true);

// ✔ Existence plutôt que Distinct : biens ayant au moins un échange
await Mesurer("APRÈS : Any (EXISTS)", db => db.Goods.Where(g => g.Id < 200_000 && g.Trades.Any()).CountAsync(), afficherSql: true);

// ✔ Intersection / différence côté SQL
await Mesurer("APRÈS : Except SQL", async db =>
    await db.Planets.Where(p => p.Id < 300_000).Select(p => p.Id)
        .Except(db.Markets.Where(m => m.PlanetId < 300_000).Select(m => m.PlanetId)).CountAsync(), afficherSql: true);
```

**Ce qu'il faut observer** : `UNION` vs `UNION ALL` dans le SQL ; l'opérateur **`Sort (Distinct Sort)` / `Hash Match (Aggregate)`** dans le plan du `Union` et son absence avec `Concat` ; les résultats **identiques** car les ensembles sont disjoints (et **différents** dès qu'ils se recoupent : essayez `a` et `b` sur la même `TradeType`).

### Sans index / avec index

`Distinct` sur une colonne **non indexée** → hachage de toutes les lignes ; sur une colonne indexée → lecture de l'index trié, sans hachage :

```sql
SET STATISTICS IO, TIME ON;
SELECT DISTINCT Quantity FROM dbo.Trades;                  -- Hash Match (Aggregate) sur 6 M de lignes
GO
CREATE INDEX IX_Lab_Trades_Qty ON dbo.Trades (Quantity);
GO
SELECT DISTINCT Quantity FROM dbo.Trades;                  -- Stream Aggregate sur l'index étroit
GO
DROP INDEX IX_Lab_Trades_Qty ON dbo.Trades;
```

### Comparaison avec une procédure stockée

Non pertinent : `UNION`/`UNION ALL` sont les mêmes instructions.

### 🧪 TP 1.7 — « Union ou Concat ? »

*Énoncé.* Écrivez la liste des identifiants de marchés qui sont **noirs** ou de `Kind == 2`. (a) avec `Union`, (b) avec `Concat` (en vous assurant de ne pas compter deux fois un marché noir de type 2), (c) avec un seul `Where` combinant les conditions. Comparez le nombre de lignes et les plans. Quelle écriture est la plus simple et la plus rapide ?

<details><summary>Correction</summary>

```csharp
await Mesurer("TP (a) Union", async db =>
    await db.Markets.Where(m => m.IsBlackMarket).Select(m => m.Id)
        .Union(db.Markets.Where(m => m.Kind == 2).Select(m => m.Id)).CountAsync(), afficherSql: true);

await Mesurer("TP (b) Concat + exclusion", async db =>
    await db.Markets.Where(m => m.IsBlackMarket).Select(m => m.Id)
        .Concat(db.Markets.Where(m => m.Kind == 2 && !m.IsBlackMarket).Select(m => m.Id)).CountAsync(), afficherSql: true);

await Mesurer("TP (c) OR", db => db.Markets.CountAsync(m => m.IsBlackMarket || m.Kind == 2), afficherSql: true);
```
Les trois donnent le même nombre. (c) ne lit la table qu'**une fois** ; (a) la lit deux fois et dédoublonne ; (b) la lit deux fois sans dédoublonner. Quand les deux conditions portent sur la **même table**, un `OR` est presque toujours préférable à un `Union`.
</details>

---

## 1.8 Partitionnement, conversion, génération

**Axe concerné : ⚙️ .NET (algorithmique en mémoire) + 🗄️ SQL (nombre de requêtes).**

### La notion

Opérateurs surtout **côté client** : `Chunk`, `Take(Range)`, `TakeWhile`/`SkipWhile` (**non traduisibles**), `Zip`, `ToLookup`, `ToDictionary`, `ToHashSet`, `Range`, `Repeat`, `Empty`, `SequenceEqual`. Ils servent à organiser les données déjà en mémoire et à **découper le travail** envoyé à la base.

### Dans quels cas on est concerné

- Traiter une longue liste d'identifiants (`Contains` avec des milliers d'éléments) : découper avec `Chunk`.
- Rechercher plusieurs fois dans une même liste (O(n·m) avec `Where` répété) : `ToLookup` / `ToDictionary` / `ToHashSet`.
- Arrêter une lecture quand une condition est atteinte (`TakeWhile`) : ne se traduit pas, donc on parcourt en flux.

### Exemple sans l'amélioration

```csharp
// ❌ Une requête par identifiant
var ids = Enumerable.Range(1, 3_000).ToList();
await Mesurer("AVANT : 1 requête par id", async db =>
{
    var n = 0;
    foreach (var id in ids) n += await db.Goods.CountAsync(g => g.Id == id);
    return n;
});

// ❌ Recherche linéaire répétée dans une liste (O(n × m))
await Mesurer("AVANT : Where répété en mémoire", async db =>
{
    var biens   = await db.Goods.AsNoTracking().Where(g => g.Id < 5_000).Select(g => new { g.Id, g.Sku }).ToListAsync();
    var echanges = await db.Trades.AsNoTracking().Where(t => t.GoodId < 5_000 && t.TradeType == 1).Select(t => t.GoodId).Take(20_000).ToListAsync();
    return echanges.Count(e => biens.Any(b => b.Id == e));
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Chunk + Contains : 3 requêtes de 1 000 identifiants
await Mesurer("APRÈS : Chunk + Contains", async db =>
{
    var n = 0;
    foreach (var lot in ids.Chunk(1_000)) n += await db.Goods.CountAsync(g => lot.Contains(g.Id));
    return n;
}, afficherSql: true);

// ✔ HashSet : recherche en O(1)
await Mesurer("APRÈS : HashSet", async db =>
{
    var biens    = await db.Goods.AsNoTracking().Where(g => g.Id < 5_000).Select(g => g.Id).ToListAsync();
    var echanges = await db.Trades.AsNoTracking().Where(t => t.GoodId < 5_000 && t.TradeType == 1).Select(t => t.GoodId).Take(20_000).ToListAsync();
    var set = biens.ToHashSet();
    return echanges.Count(set.Contains);
});

// ✔ ToLookup : regrouper une fois, interroger plusieurs fois
await Mesurer("APRÈS : ToLookup", async db =>
{
    var echanges = (await db.Trades.AsNoTracking().Where(t => t.GoodId < 5_000).Select(t => new { t.GoodId, t.Quantity }).ToListAsync())
                    .ToLookup(t => t.GoodId);
    return Enumerable.Range(1, 5_000).Sum(id => echanges[id].Sum(t => t.Quantity));
});
```

**`TakeWhile` ne se traduit pas** : le critère d'arrêt s'exprime dans le SQL par `Where` + `Take`, ou en flux :

```csharp
// ❌ exception de traduction : db.Goods.OrderBy(g => g.Id).TakeWhile(g => g.BasePrice < 99_000)
// ✔ en flux : on s'arrête dès que la condition cesse d'être vraie, le DataReader est fermé
await Mesurer("TakeWhile en flux", async db =>
{
    var n = 0;
    await foreach (var g in db.Goods.AsNoTracking().OrderBy(g => g.Id).AsAsyncEnumerable())
    { if (g.BasePrice >= 99_000) break; n++; }
    return n;
});
```

**Ce qu'il faut observer** : le nombre de requêtes (3 000 → 3), les paramètres générés par `Contains` (liste de valeurs ou paramètre JSON selon la taille), le temps d'une recherche dans une liste vs `HashSet`.

### Sans index / avec index

Sans objet côté SQL : le gain vient des allers-retours et de l'algorithmique client. (Les identifiants de `Goods.Id` sont déjà indexés par la clé primaire.)

### Comparaison avec une procédure stockée

Pour un lot d'identifiants, la procédure idiomatique prend un **paramètre table (TVP)** ; EF Core n'en gère pas nativement : c'est un cas de sortie de LINQ (chapitre 10).

### 🧪 TP 1.8 — « Lots d'identifiants »

*Énoncé.* On vous donne 10 000 identifiants de biens. Récupérez leurs SKU en émettant le **moins de requêtes raisonnable**, sans dépasser 1 000 identifiants par requête. Mesurez avec 1 identifiant par requête puis avec `Chunk(1_000)`. Pourquoi ne pas tout mettre dans un seul `Contains` ?

<details><summary>Correction</summary>

```csharp
var ids = Enumerable.Range(1, 10_000).ToList();
await Mesurer("TP (a) 1 par 1", async db =>
{
    var skus = new List<string>();
    foreach (var id in ids) skus.AddRange(await db.Goods.AsNoTracking().Where(g => g.Id == id).Select(g => g.Sku).ToListAsync());
    return skus.Count;
});
await Mesurer("TP (b) Chunk(1000)", async db =>
{
    var skus = new List<string>();
    foreach (var lot in ids.Chunk(1_000))
        skus.AddRange(await db.Goods.AsNoTracking().Where(g => lot.Contains(g.Id)).Select(g => g.Sku).ToListAsync());
    return skus.Count;
}, afficherSql: true);
```
10 000 requêtes contre 10. Un seul `Contains` de 10 000 éléments produirait un SQL énorme ou un paramètre JSON volumineux, un plan peu réutilisable et se heurte aux limites du serveur (2 100 paramètres) ; le découpage borne la taille de chaque requête et permet de les exécuter en parallèle sur des contextes distincts.
</details>

---

## 1.9 Nouveautés récentes du BCL

**Axe concerné : ⚙️ .NET (CPU et allocations, LINQ to Objects).**

### La notion

Ajouts du BCL, **côté mémoire** (pas de traduction SQL) :
- .NET 9 : `CountBy`, `AggregateBy`, `Index()`.
- .NET 10 : `LeftJoin`, `RightJoin` (LINQ to Objects ; EF Core 10/11 les traduit aussi côté `IQueryable`).
- `Order()` / `OrderDescending()` : tri sans sélecteur.

Ils **remplacent** des idiomes qui allouent des structures intermédiaires (`GroupBy` → `Select(Count)`).

### Dans quels cas on est concerné

- Comptages et sommes par clé sur des données **déjà en mémoire** (caches, résultats de requêtes volumineux).
- Outer join en mémoire.

### Exemple sans l'amélioration

```csharp
// Données chargées UNE fois (hors mesure) : 1 M de lignes de Trades en projection légère
var echanges = await NewDb().Trades.AsNoTracking().Take(1_000_000)
    .Select(t => new { t.GoodId, t.TradeType, t.Quantity }).ToListAsync();

// ❌ GroupBy matérialise un groupe par clé avec tous ses éléments
await Mesurer("AVANT : GroupBy + Count", db =>
    Task.FromResult(echanges.GroupBy(t => t.TradeType).Select(g => (g.Key, N: g.Count())).ToList().Count));
await Mesurer("AVANT : GroupBy + Sum", db =>
    Task.FromResult(echanges.GroupBy(t => t.TradeType).Select(g => (g.Key, S: g.Sum(t => (long)t.Quantity))).ToList().Count));
```

### Exemple avec l'amélioration

```csharp
// ✔ CountBy / AggregateBy : un dictionnaire d'accumulateurs, pas de groupes
await Mesurer("APRÈS : CountBy", db =>
    Task.FromResult(echanges.CountBy(t => t.TradeType).ToList().Count));
await Mesurer("APRÈS : AggregateBy", db =>
    Task.FromResult(echanges.AggregateBy(t => t.TradeType, 0L, (acc, t) => acc + t.Quantity).ToList().Count));

// ✔ LeftJoin en mémoire (.NET 10)
var biens = new[] { (Id: 1, Nom: "A"), (Id: 2, Nom: "B"), (Id: 3, Nom: "C") };
var ventes = new[] { (BienId: 1, Qte: 5), (BienId: 1, Qte: 7), (BienId: 3, Qte: 2) };
var r = biens.LeftJoin(ventes, b => b.Id, v => v.BienId, (b, v) => (b.Nom, Qte: v == default ? 0 : v.Qte));
```

**Ce qu'il faut observer** : les **allocations** (`Mo alloués`) et le temps de chaque version ; aucun SQL (la colonne « SQL » reste à 0 pour ces mesures, le chargement initial étant hors `Mesurer`).

### Sans index / avec index et procédure stockée

Sans objet : ces opérateurs travaillent en mémoire.

### 🧪 TP 1.9 — « Compter par clé, sans groupes »

*Énoncé.* Sur le même million d'échanges en mémoire, calculez le nombre d'échanges et la quantité totale **par `GoodId`** (plusieurs centaines de milliers de clés distinctes) avec (a) `GroupBy`, (b) `CountBy` + `AggregateBy`, (c) un `Dictionary` accumulé à la main avec `CollectionsMarshal.GetValueRefOrAddDefault`. Comparez temps et allocations.

<details><summary>Correction</summary>

```csharp
using System.Runtime.InteropServices;

await Mesurer("TP (a) GroupBy", db => Task.FromResult(
    echanges.GroupBy(t => t.GoodId).Select(g => (g.Key, N: g.Count(), S: g.Sum(t => (long)t.Quantity))).Count()));

await Mesurer("TP (b) CountBy + AggregateBy", db => Task.FromResult(
    echanges.CountBy(t => t.GoodId).Count() + echanges.AggregateBy(t => t.GoodId, 0L, (a, t) => a + t.Quantity).Count()));

await Mesurer("TP (c) Dictionary", db =>
{
    var d = new Dictionary<int, (int N, long S)>();
    foreach (var t in echanges)
    {
        ref var e = ref CollectionsMarshal.GetValueRefOrAddDefault(d, t.GoodId, out _);
        e = (e.N + 1, e.S + t.Quantity);
    }
    return Task.FromResult(d.Count);
});
```
Attendu : (a) est le plus allocataire (objets `Grouping`, listes internes) ; (b) fait deux passes mais n'alloue que des dictionnaires ; (c) fait une passe et la moindre allocation, au prix de la lisibilité. Moralité : **ces gains sont des gains CPU/mémoire** ; si les données sont encore en base, l'agrégation appartient à SQL (1.5).
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Inhabitants_Planet_Noms ON dbo.Inhabitants;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt_Id      ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Qty              ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Agg              ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Goods_BasePrice         ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Planets_Pop_Cover      ON dbo.Planets;
DROP PROCEDURE IF EXISTS dbo.Lab_PageHabitants;
DROP PROCEDURE IF EXISTS dbo.Lab_ResumeTrades;
DROP PROCEDURE IF EXISTS dbo.Lab_BiensPopulaires;
DROP PROCEDURE IF EXISTS dbo.Lab_PlanetesMarchesNoirs;

-- Vérification : ne doit rien retourner (en tenant compte des objets créés au chapitre 00)
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
