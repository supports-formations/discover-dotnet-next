# 02 — Traduction SQL : savoir lire et prédire

> Suite de [`01-operateurs.md`](01-operateurs.md). **Tous les extraits utilisent la boîte à outils du 00** (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans d'exécution, `ToQueryString()`).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**. Les points qui dépendent de la **version d'EF Core / de SQL Server** sont marqués *(à vérifier)*.
> Index de démonstration : préfixe `IX_Lab_` ; objets SQL : `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` **en dehors** de `Mesurer(...)` supposent que vous avez écrit `await using var db = NewDb();` plus haut dans `Program.cs`.

## Sommaire

- [2.1 Lire le SQL généré](#21-lire-le-sql-généré)
- [2.2 Fonctions .NET traduites](#22-fonctions-net-traduites)
- [2.3 `EF.Functions`](#23-effunctions)
- [2.4 Mapper une fonction SQL (UDF scalaire, fonction table)](#24-mapper-une-fonction-sql)
- [2.5 Paramètres vs constantes](#25-paramètres-vs-constantes)
- [2.6 `Contains` sur une liste locale](#26-contains-sur-une-liste-locale)
- [2.7 Comparaison de `NULL`](#27-comparaison-de-null)
- [2.8 `string.Contains` → `LIKE '%x%'`](#28-stringcontains--like-x)
- [2.9 Sargabilité](#29-sargabilité)
- [2.10 Conversions de types implicites](#210-conversions-de-types-implicites)
- [2.11 JSON](#211-json)
- [2.12 Types complexes](#212-types-complexes)
- [2.13 Fonctions de fenêtrage](#213-fonctions-de-fenêtrage)
- [Script de nettoyage](#script-de-nettoyage)

---

## 2.1 Lire le SQL généré

**Axe concerné : outillage — conditionne tout le reste (🗄️ SQL).**

### La notion

On ne corrige pas ce qu'on ne voit pas. EF Core offre quatre fenêtres sur le SQL :

| Outil | Quand | Remarque |
|---|---|---|
| `ToQueryString()` | avant exécution | SQL + déclarations de paramètres, **rien n'est envoyé** |
| `LogTo` / `ILogger` (catégorie `Database.Command`) | pendant l'exécution | SQL réellement envoyé, durée ; `EnableSensitiveDataLogging` ajoute les valeurs |
| `TagWith("…")` / `TagWithCallSite()` | à la construction | commentaire SQL pour retrouver la requête dans les DMV / Query Store |
| `ExplainPlan` côté SQL | après coup | plan réel dans l'outil SQL (voir 00) |

### Dans quels cas on est concerné

- Tout diagnostic de lenteur : d'abord **lire** le SQL, ensuite seulement mesurer.
- Requêtes d'une application en production : sans `TagWith`, impossible de relier une ligne de `sys.dm_exec_query_stats` à son code.
- Journalisation : `EnableSensitiveDataLogging` en production expose des données.

### Exemple sans l'amélioration

```csharp
// ❌ On « devine » ce qu'EF envoie
var q = db.Goods.Where(g => g.IsContraband && g.Name.Contains("Kyber")).OrderBy(g => g.BasePrice).Take(10);
var l = await q.ToListAsync();                                     // aucune trace du SQL
```
```sql
-- ❌ Côté SQL Server : des centaines de requêtes anonymes, impossibles à rattacher à un endroit du code
SELECT TOP 10 st.text, qs.execution_count FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st ORDER BY qs.total_elapsed_time DESC;
```

### Exemple avec l'amélioration

```csharp
// ✔ Voir sans exécuter
var q = db.Goods.TagWithCallSite().TagWith("demo-2.1")
          .Where(g => g.IsContraband && g.Name.Contains("Kyber")).OrderBy(g => g.BasePrice).Take(10);
Console.WriteLine(q.ToQueryString());

// ✔ Voir l'envoi réel et les valeurs de paramètres (environnement de test uniquement)
await Mesurer("avec journal", async db2 => (await q.ToListAsync()).Count, afficherSql: true);
```
```sql
-- ✔ Retrouver la requête par son tag
SELECT TOP 5 st.text, qs.execution_count, qs.total_logical_reads / qs.execution_count AS reads_moyens
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE '%demo-2.1%';
```
**À observer** : les déclarations `DECLARE @__p_0 int = 10;` en tête du SQL ; le commentaire `-- demo-2.1` ; la ligne du fichier C# ajoutée par `TagWithCallSite` ; `TOP(@p)` (le `Take` est un paramètre, EF réutilise le plan).

> `ToQueryString()` et le journal ont un coût (génération du texte, E/S console) : mesurez les performances **sans** eux.

### Sans index / avec index et procédure stockée

Sans objet : outillage.

### 🧪 TP 2.1 — « Qui a produit cette requête ? »

*Énoncé.* Exécutez trois requêtes différentes sur `Trades` (deux avec le même tag, une avec un autre), puis écrivez la requête SQL qui retourne, **par tag**, le nombre d'exécutions et la lecture moyenne en pages. Montrez comment `TagWithCallSite` aide en cas de dérive du code.

<details><summary>Correction</summary>

```csharp
await db.Trades.TagWith("tp-2.1-A").Where(t => t.GoodId == 10).ToListAsync();
await db.Trades.TagWith("tp-2.1-A").Where(t => t.GoodId == 11).ToListAsync();   // même forme, paramètre différent : une seule entrée de cache
await db.Trades.TagWith("tp-2.1-B").Where(t => t.CompanyId == 5).ToListAsync();
```
```sql
SELECT CASE WHEN st.text LIKE '%tp-2.1-A%' THEN 'tp-2.1-A' ELSE 'tp-2.1-B' END AS tag,
       SUM(qs.execution_count) AS executions,
       SUM(qs.total_logical_reads) / SUM(qs.execution_count) AS reads_moyens
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE '%tp-2.1-%' AND st.text NOT LIKE '%dm_exec_query_stats%'
GROUP BY CASE WHEN st.text LIKE '%tp-2.1-A%' THEN 'tp-2.1-A' ELSE 'tp-2.1-B' END;
```
`tp-2.1-A` apparaît avec 2 exécutions pour **un** plan : preuve que la valeur est un paramètre. `TagWithCallSite()` ajoute le chemin du fichier et la ligne : on retrouve le code même si le tag a été copié-collé.
</details>

---

## 2.2 Fonctions .NET traduites

**Axe concerné : 🗄️ SQL (où s'exécute la fonction ; sargabilité) + 🧠 mémoire.**

### La notion

EF traduit un sous-ensemble de la BCL : `string` (`Contains`, `StartsWith`, `EndsWith`, `Length`, `Substring`, `ToUpper/ToLower`, `Trim`, `Replace`, `IndexOf`, concaténation), `DateTime`/`DateOnly` (propriétés `Year`, `Month`, `Day`, `AddDays`…), `Math.*` courants, conversions simples. **Ce qui est traduit s'exécute dans la base** (bon : pas de transfert) mais une fonction appliquée à une **colonne** dans un `Where` la rend **non sargable** (voir 2.9). **Ce qui ne l'est pas** (`Split`, `string.Format`, `Regex`, méthodes maison) lève une exception hors projection finale (voir 0.5). Certains membres peuvent être non traduits selon la version/le fournisseur *(à vérifier avec `ToQueryString()`)*.

### Dans quels cas on est concerné

- Filtres sur chaînes et dates.
- Remplacer un `Split`/`Regex` par un équivalent traduisible.
- Éviter de ramener des lignes juste pour appliquer un formatage.

### Exemple sans l'amélioration

```csharp
// ❌ Split non traduisible → on est obligé de rapatrier les sociétés (1 M)
await Mesurer("AVANT : Split côté client", db =>
    Task.FromResult(db.Companies.AsNoTracking().AsEnumerable().Count(c => c.Name.Split(' ')[0].Length > 8)));

// ❌ Fonction sur la colonne de date (sargabilité, voir 2.9)
await Mesurer("AVANT : Year/Month sur la colonne", db =>
    db.Trades.CountAsync(t => t.TradedAt.Year == 2025 && t.TradedAt.Month == 3), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ IndexOf/Substring sont traduits (CHARINDEX/SUBSTRING) : le calcul reste dans SQL
await Mesurer("APRÈS : IndexOf traduit", db =>
    db.Companies.CountAsync(c => c.Name.IndexOf(' ') > 8), afficherSql: true);

// ✔ Intervalle de dates : aucune fonction sur la colonne
var debut = new DateTime(2025, 3, 1); var fin = debut.AddMonths(1);
await Mesurer("APRÈS : intervalle", db => db.Trades.CountAsync(t => t.TradedAt >= debut && t.TradedAt < fin), afficherSql: true);
```
**À observer** : `CHARINDEX(N' ', [c].[Name]) - 1 > 8` dans le SQL (ou la forme exacte de votre version) ; `DATEPART(year, …)` contre `>= @debut AND < @fin`. Testez d'autres membres avec `ToQueryString()` (`DayOfWeek`, `DayOfYear`, `ToString()`, `Math.Round`, `string.Join`) : s'ils ne sont pas traduits, EF le dit avec le message *« could not be translated »*.

### Sans index / avec index

La différence index/pas d'index apparaît au 2.9 (prédicats de date). Ici, l'enjeu est **où** la fonction s'exécute.

### Comparaison avec une procédure stockée

Sans objet : la procédure appliquerait les mêmes fonctions T-SQL.

### 🧪 TP 2.2 — « Traduire ou rapatrier ? »

*Énoncé.* Pour chaque expression, dites avec `ToQueryString()` si elle est traduite et, sinon, proposez un équivalent traduisible : `c.Name.Split(' ')[0]`, `c.Name.ToUpper()`, `t.TradedAt.DayOfWeek`, `Math.Round(t.UnitPrice, 1)`, `string.Format("{0}-{1}", g.Sku, g.Name)`, `g.Sku + "-" + g.Name`.

<details><summary>Correction</summary>

Méthode : ajouter `.Select(x => <expression>).Take(1).ToQueryString()` pour chaque cas. Attendu en général : `ToUpper` → `UPPER`, `Math.Round` → `ROUND`, la concaténation `+` → `+`/`CONCAT`, `Split` et `string.Format` **non traduits** (exception ou évaluation client dans la projection finale), `DayOfWeek` selon la version *(à vérifier)*. Équivalents : `Split(' ')[0]` → `Substring(0, IndexOf(' '))` ; `string.Format` → concaténation ou interpolation que EF sait traduire en `+` ; `DayOfWeek` → `EF.Functions.DateDiffDay` ou une colonne calculée.
</details>

---

## 2.3 `EF.Functions`

**Axe concerné : 🗄️ SQL.**

### La notion

`EF.Functions` expose des fonctions **propres à la base** que LINQ standard n'a pas : `Like`, `Contains`/`FreeText` (recherche **plein texte**), `DateDiffDay`/`DateDiffHour`…, `Collate`, `Random`, `IsNullOrWhiteSpace` selon version… Elles s'exécutent côté SQL et permettent d'éviter l'évaluation client **et** de choisir le bon opérateur (`LIKE` contrôlé, plein texte, collation).

### Dans quels cas on est concerné

- Motifs `LIKE` avec jokers (`_`, `[…]`) non exprimables avec `Contains`/`StartsWith`.
- Recherche de texte sur de gros volumes (plein texte au lieu de `LIKE '%x%'`).
- Écarts de dates calculés en SQL.
- Comparaison sensible à la casse sur une base insensible.

### Exemple sans l'amélioration

```csharp
// ❌ Un motif avec joker simple, écrit en mémoire
var rx = new System.Text.RegularExpressions.Regex("^Kyber .urasteel$");
await Mesurer("AVANT : Regex client", db =>
    Task.FromResult(db.Goods.AsNoTracking().AsEnumerable().Count(g => rx.IsMatch(g.Name))));

// ❌ Écart de jours calculé côté client
await Mesurer("AVANT : écart en mémoire", db =>
    Task.FromResult(db.Markets.AsNoTracking().Take(500_000).AsEnumerable()
        .Count(m => (new DateOnly(2025, 12, 31).DayNumber - m.OpenedOn.DayNumber) > 365 * 10)));
```

### Exemple avec l'amélioration

```csharp
// ✔ LIKE avec joker « _ »
await Mesurer("APRÈS : EF.Functions.Like", db => db.Goods.CountAsync(g => EF.Functions.Like(g.Name, "Kyber _urasteel")), afficherSql: true);

// ✔ DateDiffDay côté SQL
await Mesurer("APRÈS : DateDiffDay", db =>
    db.Markets.CountAsync(m => EF.Functions.DateDiffDay(m.OpenedOn, new DateOnly(2025, 12, 31)) > 365 * 10), afficherSql: true);

// ✔ Comparaison sensible à la casse sur une base insensible
await Mesurer("Collate CS", db => db.Goods.CountAsync(g => EF.Functions.Collate(g.Sku, "Latin1_General_CS_AS") == "GD-0000000001"), afficherSql: true);
```
**Plein texte** (à n'essayer que si la fonctionnalité est installée : `SELECT SERVERPROPERTY('IsFullTextInstalled');` doit retourner `1` ; l'image Docker standard de SQL Server **ne l'inclut pas** — dans ce cas, lisez simplement le code) :

```sql
CREATE FULLTEXT CATALOG LabCat;
CREATE FULLTEXT INDEX ON dbo.Goods (Name) KEY INDEX PK_Goods ON LabCat;     -- nom exact de la clé primaire à adapter
```
```csharp
await Mesurer("plein texte", db => db.Goods.CountAsync(g => EF.Functions.Contains(g.Name, "Kyber")));
```
**À observer** : le SQL (`LIKE N'Kyber _urasteel'`, `DATEDIFF(day, …)`, `COLLATE Latin1_General_CS_AS`) ; le coût (`Collate` impose un scan : l'index de la colonne est trié selon la collation insensible).

### Sans index / avec index

`DATEDIFF` sur une colonne n'est **pas sargable** (comme toute fonction sur colonne) ; `LIKE 'préfixe%'` l'est. Comparez avec `IX_Lab_Goods_Name` (voir 0.5).

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 2.3 — « Insensible ou sensible à la casse ? »

*Énoncé.* La base est en collation CI. Retrouvez (a) le bien dont le SKU est exactement `GD-0000000001` en respectant la casse, (b) les biens dont le nom est `kyber durasteel` sans respecter la casse. Donnez le SQL de chaque cas et dites lequel peut utiliser l'index sur `Sku`.

<details><summary>Correction</summary>

```csharp
// (a) sensible à la casse : Collate → scan (la collation CS ne correspond pas à celle de l'index)
await Mesurer("TP (a)", db => db.Goods.CountAsync(g => EF.Functions.Collate(g.Sku, "Latin1_General_CS_AS") == "GD-0000000001"), afficherSql: true);
// (b) insensible : l'égalité simple suffit (collation CI)
await Mesurer("TP (b)", db => db.Goods.CountAsync(g => g.Name == "kyber durasteel"), afficherSql: true);
```
(a) force une collation différente de celle de l'index : le plan montre un scan. (b) s'écrit sans `ToLower()`. Pour que (a) soit sargable, il faudrait une colonne ou un index construits avec la collation CS (colonne calculée + index).
</details>

---

## 2.4 Mapper une fonction SQL

**Axe concerné : 🗄️ SQL (exécution dans la base) — attention au coût des UDF.**

### La notion

Pour appeler une fonction SQL (UDF scalaire ou fonction table) depuis LINQ : méthode statique marquée `[DbFunction]` (ou `HasDbFunction`). EF la traduit en appel de fonction ; la fonction **s'exécute dans la base**, sans rapatrier les lignes. **Piège** : une UDF scalaire peut être appelée **ligne par ligne** et bloquer le parallélisme (selon la version et le niveau de compatibilité, SQL Server peut l'« inliner »). Une **fonction table** (TVF) se compose avec LINQ (`CROSS APPLY`).

### Dans quels cas on est concerné

- Logique métier déjà écrite en T-SQL à réutiliser.
- Calculs qu'EF ne traduit pas (formules, règles).
- Alternative à `FromSql` quand on veut **composer** avec d'autres opérateurs LINQ.

### Exemple sans l'amélioration

```csharp
// ❌ Formule non traduisible → rapatrier puis calculer
static decimal PrixTTC(decimal prix, decimal taux) => prix * (1 + taux);

await Mesurer("AVANT : calcul client", async db =>
    (await db.Goods.AsNoTracking().Select(g => new { g.Id, g.BasePrice }).ToListAsync())
        .Count(g => PrixTTC(g.BasePrice, 0.2m) > 90_000m));
```

### Exemple avec l'amélioration

```sql
CREATE OR ALTER FUNCTION dbo.Lab_PrixTTC (@prix decimal(18,2), @taux decimal(5,4))
RETURNS decimal(18,2)
WITH SCHEMABINDING
AS
BEGIN
    RETURN @prix * (1 + @taux);
END
GO
CREATE OR ALTER FUNCTION dbo.Lab_EchangesDuBien (@GoodId int)
RETURNS TABLE
AS RETURN (SELECT Id, MarketId, CompanyId, GoodId, TradeType, Quantity, UnitPrice, TradedAt FROM dbo.Trades WHERE GoodId = @GoodId);
```
Dans votre projet, **complétez** la classe partielle du contexte (fichier à vous) :

```csharp
public partial class StarWarsContext
{
    [DbFunction("Lab_PrixTTC", "dbo")]
    public static decimal PrixTTC(decimal prix, decimal taux) => throw new NotSupportedException();

    [DbFunction("Lab_EchangesDuBien", "dbo")]
    public IQueryable<Trade> EchangesDuBien(int goodId) => FromExpression(() => EchangesDuBien(goodId));
}
```
```csharp
await Mesurer("APRÈS : UDF scalaire", db => db.Goods.CountAsync(g => StarWarsContext.PrixTTC(g.BasePrice, 0.2m) > 90_000m), afficherSql: true);

// ✔ Fonction table composable : TVF + Where + agrégat
await Mesurer("TVF", async db => (await db.EchangesDuBien(424_242).Where(t => t.TradeType == 1).CountAsync()), afficherSql: true);
```
**À observer** : le SQL (`dbo.Lab_PrixTTC(...)`, `dbo.Lab_EchangesDuBien(@goodId)`) ; comparez **le même filtre écrit directement en LINQ** (`g.BasePrice * 1.2m > 90_000m`) : l'UDF est-elle plus rapide ? Dans le plan, regardez si l'UDF est inlinée ou apparaît comme `Compute Scalar` évalué pour chaque ligne. Pour la TVF, comparez à `g.Trades` en `CROSS APPLY` sur plusieurs biens.

### Sans index / avec index

`PrixTTC(BasePrice, 0.2) > 90000` est une fonction sur colonne : **non sargable**. La réécriture `BasePrice > 90000 / 1.2` l'est. Mesurez avec `IX_Lab_Goods_BasePrice` (voir 1.4) : le calcul sur la colonne ignore l'index, la version réécrite l'utilise.

### Comparaison avec une procédure stockée

Une procédure ne se compose pas avec LINQ ; une **TVF** si. À retenir : la TVF permet de garder la logique en SQL **et** de la filtrer/trier/paginer en LINQ, la procédure non.

### 🧪 TP 2.4 — « UDF ou expression ? »

*Énoncé.* Comparez trois formes de « prix TTC > 90 000 » sur `Goods` : UDF mappée, expression LINQ directe `g.BasePrice * 1.2m`, et réécriture `g.BasePrice > 90_000m / 1.2m`. Avec/sans `IX_Lab_Goods_BasePrice`. Relevez le plan et les lectures.

<details><summary>Correction</summary>

Les deux premières formes appliquent un calcul sur la colonne : scan, **même avec l'index**. La troisième déplace le calcul côté constante : **seek** sur l'index. L'UDF n'apporte aucun avantage de performance sur l'expression directe ; elle se justifie par la **réutilisation** d'une règle métier ou quand EF ne sait pas la traduire.
</details>

---

## 2.5 Paramètres vs constantes

**Axe concerné : 🗄️ SQL (plans en cache, *parameter sniffing*) + ⚙️ .NET (cache de requêtes EF).**

### La notion

- Une **variable capturée** devient un **paramètre SQL** : une seule forme de requête, un seul plan, réutilisé.
- Une **constante** (`EF.Constant(x)`, valeur écrite en dur, `Expression.Constant`) est inlinée dans le SQL : **un texte et un plan par valeur** — pollution du cache de plans (bloat) et du cache de requêtes EF.
- *Inversement*, le paramètre peut provoquer le **parameter sniffing** : le plan compilé pour la 1ʳᵉ valeur est réutilisé pour des valeurs de tout autre profil (très sélectives / très larges). Une constante, ou `OPTION (RECOMPILE)`, donne un plan par valeur.
- `EF.Parameter(x)` force le paramètre là où EF aurait inliné ; `EF.Constant(x)` force la constante. *(Versions : à vérifier.)*

### Dans quels cas on est concerné

- Valeurs interpolées à la main, expressions construites dynamiquement avec des constantes : bloat.
- Colonnes à **distribution très asymétrique** (ex. `Planets.Population`) : sniffing.

### Exemple sans l'amélioration

```csharp
// ❌ 300 formes de requêtes, 300 plans en cache
await Mesurer("AVANT : EF.Constant ×300", async db =>
{
    long s = 0;
    for (var i = 1; i <= 300; i++) { var id = i; s += (await db.Goods.AsNoTracking().Where(g => g.Id == EF.Constant(id)).FirstAsync()).Id; }
    return s;
});
```
```sql
DBCC FREEPROCCACHE;     -- avant (conteneur de test)
-- exécuter la boucle C#, puis :
SELECT COUNT(*) AS plans FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
WHERE st.text LIKE '%FROM [[]Goods]%' AND st.text NOT LIKE '%dm_exec_cached_plans%';
```

### Exemple avec l'amélioration

```csharp
// ✔ Paramètre : un seul plan
await Mesurer("APRÈS : paramètre ×300", async db =>
{
    long s = 0;
    for (var i = 1; i <= 300; i++) { var id = i; s += (await db.Goods.AsNoTracking().Where(g => g.Id == id).FirstAsync()).Id; }
    return s;
});
```
**Parameter sniffing** (le cas où la constante est *meilleure*) :

```sql
CREATE INDEX IX_Lab_Planets_Population ON dbo.Planets (Population);       -- index SANS INCLUDE : lookups pour les autres colonnes
DBCC FREEPROCCACHE;
```
```csharp
// Calibrez d'abord deux seuils : « étroit » (~1 000 lignes) et « large » (plusieurs centaines de milliers)
var etroit = 99_000_000_000L; var large = 1_000_000_000L;
static IQueryable<Planet> Q(StarWarsContext db, long seuil) =>
    db.Planets.AsNoTracking().Where(p => p.Population > seuil).Select(p => p);

await Mesurer("étroit d'abord, puis large (même plan réutilisé)", async db =>
{
    var a = (await Q(db, etroit).ToListAsync()).Count;      // le plan « seek + lookups » est compilé pour cette valeur
    var b = (await Q(db, large).ToListAsync()).Count;       // …et réutilisé pour une valeur large : lookups massifs
    return $"{a} + {b}";
});
// Variante : seuil en constante → un plan par valeur
//   .Where(p => p.Population > EF.Constant(seuil))
```
**À observer** : dans le plan réel de la 2ᵉ requête, **lignes estimées ≪ lignes réelles** (estimation héritée de la 1ʳᵉ valeur) et un nombre de lectures disproportionné ; avec la constante, chaque valeur obtient son plan (scan pour le seuil large). Pour la solution générale (indicateur de requête `OPTION (RECOMPILE)` via intercepteur), voir le chapitre 7.

### Sans index / avec index

Le sniffing n'existe que si un **index offre plusieurs plans possibles** (seek+lookup vs scan) : sans l'index `IX_Lab_Planets_Population`, le plan est un scan dans tous les cas et le problème disparaît.

### Comparaison avec une procédure stockée

Une procédure stockée **est** soumise au sniffing (plan compilé à la 1ʳᵉ exécution). Contre-mesures : `OPTION (RECOMPILE)`, `OPTIMIZE FOR`, procédures distinctes par profil. LINQ n'a pas ces options en natif (voir 7.5).

### 🧪 TP 2.5 — « Le plan qui ne convient pas »

*Énoncé.* Reproduisez le sniffing : videz le cache de plans, exécutez la requête avec le seuil étroit puis large, relevez les *logical reads* de la 2ᵉ exécution. Réexécutez dans l'**ordre inverse** (large puis étroit) après un `DBCC FREEPROCCACHE`. Expliquez la différence.

<details><summary>Correction</summary>

Dans l'ordre « étroit puis large », le plan en cache est un *seek + key lookups* : la requête large le suit et fait des centaines de milliers de lookups (lectures très supérieures à un scan). Dans l'ordre « large puis étroit », le plan en cache est un **scan** : la requête étroite lit toute la table alors qu'un seek suffisait. Le même code est donc rapide ou lent **selon la première valeur vue après le vidage du cache** — d'où l'intérêt de reconnaître le symptôme (estimations ≠ réel) avant de choisir paramètre, constante ou `RECOMPILE`.
</details>

---

## 2.6 `Contains` sur une liste locale

**Axe concerné : 🗄️ SQL (forme du SQL, plans) + ⚙️ .NET (génération).**

### La notion

`ids.Contains(x.Id)` avec `ids` locale se traduit en `IN (…)`. La **façon dont EF transmet la liste** a évolué au fil des versions : constantes inlinées, paramètre JSON lu par `OPENJSON`, paramètres scalaires multiples. Chaque stratégie a ses effets : **nombre de plans** (une liste de taille variable = un texte différent), **limite de 2 100 paramètres** de SQL Server, **estimation de cardinalité** (avec `OPENJSON`, l'optimiseur estime mal la taille de la liste). Des options existent pour choisir *(à vérifier selon votre version)* : `UseParameterizedCollectionMode(ParameterTranslationMode.…)` (EF 10 : `MultipleParameters` par défaut, `Parameter` = JSON/`OPENJSON` (défaut d'EF 8-9), `Constant`) et les méthodes `EF.Constant(liste)`, `EF.Parameter(liste)`, `EF.MultipleParameters(liste)`.

### Dans quels cas on est concerné

- Filtrer par une liste d'identifiants venue du client ou d'une autre requête.
- Listes de tailles très variables (cache de plans polluée).
- Listes de milliers d'éléments.

### Exemple sans l'amélioration

```csharp
// ❌ Liste très grosse, d'un coup
var ids = Enumerable.Range(1, 5_000).ToList();
await Mesurer("AVANT : Contains 5 000", db => db.Goods.CountAsync(g => ids.Contains(g.Id)), afficherSql: true);

// ❌ Une requête par id
await Mesurer("AVANT : 1 par 1", async db => { var n = 0; foreach (var id in ids.Take(500)) n += await db.Goods.CountAsync(g => g.Id == id); return n; });
```

### Exemple avec l'amélioration

```csharp
// ✔ Observer la forme du SQL pour 10, 100, 1 000, 5 000 éléments
foreach (var n in new[] { 10, 100, 1_000, 5_000 })
{
    var liste = Enumerable.Range(1, n).ToList();
    await using var db = NewDb();
    var q = db.Goods.Where(g => liste.Contains(g.Id));
    var sql = q.ToQueryString();
    Console.WriteLine($"{n,5} éléments → {sql.Length,7} caractères de SQL ; extrait : {sql[..Math.Min(160, sql.Length)].Replace('\n', ' ')}");
}

// ✔ Découper en lots (Chunk) pour borner la taille de chaque requête
await Mesurer("APRÈS : Chunk(1000)", async db =>
{
    var n = 0; foreach (var lot in ids.Chunk(1_000)) n += await db.Goods.CountAsync(g => lot.Contains(g.Id));
    return n;
}, afficherSql: true);
```
Choix explicite du mode *(si disponible dans votre version)* :

```csharp
// dans NewDb() : .UseSqlServer(Cs, o => o.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter))     // ou Constant / MultipleParameters
// ou par requête : db.Goods.Where(g => EF.Parameter(ids).Contains(g.Id)) / EF.Constant(ids) / EF.MultipleParameters(ids)
```
**À observer** : pour 10 éléments, `IN (@ids1, @ids2, …)` ou `IN (SELECT value FROM OPENJSON(@ids))` selon la version ; la **taille du SQL** quand les valeurs sont inlinées ; le plan des deux formes (estimation du nombre de lignes de `OPENJSON` vs nombre réel) ; le nombre de plans en cache après avoir lancé 4 tailles différentes.

### Sans index / avec index

`Goods.Id` est la clé primaire : `Contains` sur `Id` est un *seek* par valeur. Sur une colonne **non indexée** (ex. `Goods.Name IN (…)`), c'est un scan pour toute liste ; testez avec `IX_Lab_Goods_Name`.

### Comparaison avec une procédure stockée

L'équivalent SQL pour une longue liste est un **paramètre table (TVP)** ou une table temporaire jointe. EF Core ne gère pas les TVP nativement : voir le chapitre 10.

### 🧪 TP 2.6 — « Quatre tailles de liste »

*Énoncé.* Exécutez le `Contains` pour 10, 100, 1 000 et 5 000 éléments. Pour chaque taille, relevez : forme du SQL, nombre de paramètres, temps. Après les quatre exécutions, comptez les plans en cache pour `Goods`. Concluez sur le meilleur compromis entre une seule grosse requête et des lots.

<details><summary>Correction</summary>

Méthode : `DBCC FREEPROCCACHE` ; boucle sur les quatre tailles avec `Mesurer` et `afficherSql: true` ; requête de comptage des plans (voir 2.5). Selon votre mode de traduction, vous observerez soit autant de plans que de tailles (listes inlinées ou paramètres multiples), soit un plan unique (paramètre JSON). Conclusion habituelle : lots de quelques centaines à ~1 000 éléments, avec des tailles **normalisées** (ex. compléter les lots à 100/500/1 000 par répétition d'un élément) pour limiter le nombre de plans.
</details>

---

## 2.7 Comparaison de `NULL`

**Axe concerné : 🗄️ SQL (prédicat `OR … IS NULL`, usage d'index) et justesse.**

### La notion

C# : `null == null` est vrai. SQL : `NULL = NULL` est *inconnu*. Pour que les résultats suivent la sémantique C#, EF ajoute des tests `IS NULL`. Cela **alourdit** le prédicat et peut **empêcher un seek** sur la colonne. Trois attitudes :
1. laisser faire (sémantique C#) ;
2. écrire **explicitement** la condition (`!= null &&`) ;
3. `UseRelationalNulls()` : sémantique SQL pure pour tout le contexte.

### Dans quels cas on est concerné

- `!=` sur une colonne nullable, `==` entre deux colonnes nullables.
- Paramètre nullable (`int? factionId`).
- `Contains` sur une liste contenant `null`.

### Exemple sans l'amélioration

```csharp
// ❌ != sur une colonne nullable : EF ajoute « OR [MidiChlorians] IS NULL » → 4 M de lignes
await Mesurer("AVANT : != 7000", db => db.Inhabitants.CountAsync(i => i.MidiChlorians != 7000), afficherSql: true);

// ❌ Liste contenant un null : « IN (…) OR IS NULL » → ~30 % des habitants n'ont pas de faction
var factions = new List<int?> { 1, 2, 3, null };
await Mesurer("AVANT : Contains avec null", db => db.Inhabitants.CountAsync(i => factions.Contains(i.FactionId)), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Condition explicite
await Mesurer("APRÈS : != null && !=", db => db.Inhabitants.CountAsync(i => i.MidiChlorians != null && i.MidiChlorians != 7000), afficherSql: true);

// ✔ Liste sans null
var sansNull = new List<int> { 1, 2, 3 };
await Mesurer("APRÈS : Contains sans null", db => db.Inhabitants.CountAsync(i => sansNull.Contains(i.FactionId!.Value)), afficherSql: true);

// ✔ Paramètre nullable : comparez le SQL quand la variable est nulle et quand elle ne l'est pas
int? fid = null;  Console.WriteLine(NewDb().Inhabitants.Where(i => i.FactionId == fid).ToQueryString());
fid = 42;        Console.WriteLine(NewDb().Inhabitants.Where(i => i.FactionId == fid).ToQueryString());
// ✔ Sémantique SQL pour tout le contexte : dans NewDb(), .UseSqlServer(Cs, o => o.UseRelationalNulls())
```
**À observer** : le SQL de chaque variante (`OR … IS NULL`, `IS NULL` quand le paramètre est nul, `= @fid` sinon) ; les **résultats** (≈ 4 M contre ≈ 1 M ; ≈ 30 % de plus avec le `null` dans la liste) ; si le temps change ou non (souvent non quand il n'y a pas d'index).

### Sans index / avec index

`FactionId` possède un index (clé étrangère) : comparez les plans de `i.FactionId != 5` (sémantique C#, `<> 5 OR IS NULL`) et de `i.FactionId != null && i.FactionId != 5`. Le premier parcourt l'index dans les deux sens ou scanne ; le second restreint à `IS NOT NULL`. Vérifiez dans le plan : *Seek Predicates* ou simple *Predicate*.

### Comparaison avec une procédure stockée

En T-SQL on écrit **explicitement** `IS NULL` ou `ISNULL` : la sémantique est dans la procédure et ne dépend pas d'une option EF. C'est l'une des rares raisons « de fond » de préférer SQL écrit à la main quand les `NULL` sont centraux.

### 🧪 TP 2.7 — « Le filtre facultatif de faction »

*Énoncé.* Écrivez une recherche `ParFaction(int? factionId)` : si l'argument est non nul, retourner les habitants de cette faction ; s'il est nul, **ceux sans faction**. Vérifiez avec `ToQueryString()` que le SQL est un simple `IS NULL` / `=` (pas de `OR`) dans les deux cas.

<details><summary>Correction</summary>

```csharp
static IQueryable<Inhabitant> ParFaction(StarWarsContext db, int? factionId) =>
    factionId is null ? db.Inhabitants.Where(i => i.FactionId == null)
                      : db.Inhabitants.Where(i => i.FactionId == factionId);
Console.WriteLine(ParFaction(db, null).ToQueryString());   // WHERE [FactionId] IS NULL
Console.WriteLine(ParFaction(db, 42).ToQueryString());     // WHERE [FactionId] = @factionId
```
Choisir la branche **en C#** (hors de l'expression) évite de confier à EF un prédicat à double sens. Certaines versions réduisent déjà le prédicat selon la valeur du paramètre : comparez avec `i.FactionId == factionId` seul.
</details>

---

## 2.8 `string.Contains` → `LIKE '%x%'`

**Axe concerné : 🗄️ SQL (scan de toutes les valeurs).**

### La notion

`Contains("x")` devient `LIKE N'%x%'` : le motif **commence par un joker**, donc aucun *seek* possible — chaque valeur est examinée. `StartsWith("x")` devient `LIKE N'x%'` : **sargable** (un index sur la colonne permet un seek par plage). `EndsWith` : joker au début, non sargable. Alternatives pour les recherches « contient » sur de gros volumes : **plein texte** (`EF.Functions.Contains`/`FreeText`), colonnes calculées + index, stockage du texte inversé pour les `EndsWith`, moteur de recherche externe (chapitre 10).

### Dans quels cas on est concerné

- Zones de recherche libre.
- `Contains` sur de grosses tables, appelé fréquemment.

### Exemple sans l'amélioration

```csharp
await Mesurer("AVANT : Contains", db => db.Goods.CountAsync(g => g.Name.Contains("Kyber")), afficherSql: true);   // LIKE N'%Kyber%'
await Mesurer("AVANT : EndsWith", db => db.Goods.CountAsync(g => g.Name.EndsWith("steel")), afficherSql: true);  // LIKE N'%steel'
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : StartsWith", db => db.Goods.CountAsync(g => g.Name.StartsWith("Kyber")), afficherSql: true);   // LIKE N'Kyber%'
```
**À observer** : le motif dans le SQL ; dans le plan, `Clustered Index Scan` pour `Contains`/`EndsWith`, et — avec l'index ci-dessous — un **Index Seek** (plage) pour `StartsWith`. Notez aussi que `StartsWith("Kyber")` correspond à *beaucoup* de lignes : le seek lit une plage large ; plus le préfixe est sélectif, plus le gain est grand.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
-- sans index : les trois motifs scannent la table
CREATE INDEX IX_Lab_Goods_Name ON dbo.Goods (Name);
-- avec index :
--   '%Kyber%'  → Index Scan de l'index étroit (moins de pages qu'avant, mais tout est lu)
--   'Kyber%'   → Index Seek
DROP INDEX IX_Lab_Goods_Name ON dbo.Goods;
```
**Astuce pour `EndsWith`** : colonne calculée persistée inversée + index :
```sql
ALTER TABLE dbo.Goods ADD NomInverse AS REVERSE(Name);
CREATE INDEX IX_Lab_Goods_NomInverse ON dbo.Goods (NomInverse);
-- WHERE NomInverse LIKE REVERSE('steel') + '%'
```

### Comparaison avec une procédure stockée

Sans objet : le motif `LIKE` est le même.

### 🧪 TP 2.8 — « Quatre façons de chercher dans les noms »

*Énoncé.* Comptez les biens dont le nom contient `Kyber` avec : `Contains`, `StartsWith`, `EF.Functions.Like` avec `%Kyber%`, et (si installé) le plein texte. Pour chacune, donnez le SQL et le plan, avec et sans `IX_Lab_Goods_Name`.

<details><summary>Correction</summary>

`Contains` et `Like('%Kyber%')` génèrent le **même** motif (scan) ; `StartsWith` est sargable. Avec l'index, `Contains` lit l'index étroit au lieu de la table (gain de pages, mais pas de seek) ; `StartsWith` fait un seek. Le plein texte (si installé) se comporte tout autrement : recherche dans l'index inversé de mots, indépendante de la position, au prix d'une maintenance d'index spécifique et d'une sémantique (mots, racines) différente de `LIKE`.
</details>

---

## 2.9 Sargabilité

**Axe concerné : 🗄️ SQL (seek vs scan) — c'est la règle de performance n° 1 des prédicats.**

### La notion

Un prédicat est **sargable** si SQL Server peut l'évaluer en **cherchant dans un index** (*Search ARGument ABLE*). Il cesse de l'être dès qu'on applique une **fonction ou un calcul sur la colonne** : `YEAR(col) = 2025`, `LOWER(col) = …`, `col * 2 > 900`, `DATEDIFF(...)`, conversion implicite, `LIKE '%x'`. On **déplace le calcul du côté de la constante** : `col >= '2025-01-01' AND col < '2026-01-01'`, `col > 450`.

### Dans quels cas on est concerné

- Prédicats sur dates (`Year`, `Month`, `Date`), chaînes (`ToLower`, `Substring`), nombres (arithmétique).
- Filtres sur colonnes indexées appelées souvent.

### Exemple sans l'amélioration

```csharp
// ❌ Fonctions sur la colonne
await Mesurer("AVANT : Year/Month", db => db.Trades.CountAsync(t => t.TradedAt.Year == 2025 && t.TradedAt.Month == 3), afficherSql: true);
await Mesurer("AVANT : Quantity * 2", db => db.Trades.CountAsync(t => t.Quantity * 2 > 990), afficherSql: true);
await Mesurer("AVANT : ToLower", db => db.Goods.CountAsync(g => g.Sku.ToLower() == "gd-0000424242"), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
var debut = new DateTime(2025, 3, 1); var fin = debut.AddMonths(1);
await Mesurer("APRÈS : intervalle", db => db.Trades.CountAsync(t => t.TradedAt >= debut && t.TradedAt < fin), afficherSql: true);
await Mesurer("APRÈS : Quantity > 495", db => db.Trades.CountAsync(t => t.Quantity > 495), afficherSql: true);
await Mesurer("APRÈS : Sku ==", db => db.Goods.CountAsync(g => g.Sku == "gd-0000424242"), afficherSql: true);

// Cas piège inverse : ça ressemble à une fonction sur la colonne, mais SQL Server la réécrit en plage
var jour = new DateTime(2025, 3, 1);
await Mesurer(".Date == jour", db => db.Trades.CountAsync(t => t.TradedAt.Date == jour), afficherSql: true);
```
**À observer** : `DATEPART(year, …)` contre intervalle ; `Index Scan` contre `Index Seek` dans le plan **avec l'index** ; pour `.Date == jour`, le plan : SQL Server transforme `CAST(TradedAt AS date) = @jour` en recherche d'intervalle *(à vérifier dans votre plan)*.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                       -- scan clustered
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                  -- scan aussi (pas d'index)
GO
CREATE INDEX IX_Lab_Trades_TradedAt ON dbo.Trades (TradedAt);
GO
SELECT COUNT(*) FROM dbo.Trades WHERE YEAR(TradedAt) = 2025 AND MONTH(TradedAt) = 3;                       -- Index Scan (tout l'index)
SELECT COUNT(*) FROM dbo.Trades WHERE TradedAt >= '2025-03-01' AND TradedAt < '2025-04-01';                  -- Index Seek (une plage)
GO
DROP INDEX IX_Lab_Trades_TradedAt ON dbo.Trades;
```
**Leçon** : l'index seul ne suffit pas, l'écriture du prédicat est la **moitié** du gain ; et l'écriture sargable sans index ne fait rien non plus. Il faut les deux.

### Comparaison avec une procédure stockée

Les mêmes règles s'appliquent dans une procédure : `WHERE YEAR(col) = @a` n'y est pas plus sargable. La procédure n'est pas une solution à un prédicat mal écrit.

### 🧪 TP 2.9 — « Corriger cinq prédicats »

*Énoncé.* Réécrivez de manière sargable puis validez avec/sans index :
1. `t.TradedAt.Year == 2024`
2. `t.TradedAt.Date >= new DateTime(2024,1,1)` *(déjà sargable ? vérifiez)*
3. `i.BirthYear + 30 > 2000` sur `Inhabitants`
4. `g.Sku.Substring(0, 3) == "GD-"` (préfixe)
5. `g.Name.ToUpper() == "KYBER DURASTEEL"`

<details><summary>Correction</summary>

1. `TradedAt >= 2024-01-01 && TradedAt < 2025-01-01`.
2. `TradedAt >= 2024-01-01` (l'opérateur `.Date` sur une comparaison `>=` n'apporte rien : l'écriture simple est plus lisible et sûre).
3. `BirthYear > 1970`.
4. `g.Sku.StartsWith("GD-")` (`LIKE 'GD-%'`, sargable).
5. `g.Name == "kyber durasteel"` (collation insensible) — pas de `ToUpper()`.
Pour chacune : `ToQueryString()` avant/après, puis plan avec/sans index approprié (`TradedAt`, `BirthYear`, `Sku` déjà indexé unique, `Name`).
</details>

---

## 2.10 Conversions de types implicites

**Axe concerné : 🗄️ SQL (scan silencieux) + exactitude.**

### La notion

EF mappe `string` vers `nvarchar` par défaut. Si la colonne SQL est en **`varchar`** (c'est le cas de `Goods.Sku` et `Sectors.Code`), le paramètre envoyé est `nvarchar` : SQL Server doit **convertir implicitement** pour comparer. Selon la **collation** de la colonne et le sens de la conversion, cela peut **empêcher le seek** (`CONVERT_IMPLICIT` visible dans le plan, avertissement `PlanAffectingConvert`) ou être absorbé par SQL Server (avec une collation SQL comme `SQL_Latin1_General_CP1_CI_AS`, un seek dynamique reste possible). **À vérifier dans le plan** de votre colonne. Solutions : déclarer le type dans le modèle (`IsUnicode(false)` / `HasColumnType("varchar(20)")`), ou typer le paramètre explicitement en SQL brut.

### Dans quels cas on est concerné

- Colonnes `varchar` mappées sans `IsUnicode(false)` (modèle généré sans précision).
- `int` contre `bigint`, `decimal` de précisions différentes, `date` contre `datetime`.
- Clés jointes de types différents.

### Exemple sans l'amélioration

```csharp
// ❌ Paramètre nvarchar sur une colonne varchar : regardez le plan
await Mesurer("AVANT : Sku == (paramètre nvarchar)", async db =>
{
    var sku = "GD-0000424242";
    return await db.Goods.CountAsync(g => g.Sku == sku);
}, afficherSql: true);                                   // @sku nvarchar(4000)

await Mesurer("AVANT : Code == (Sectors, varchar)", async db =>
{
    var code = (await db.Sectors.AsNoTracking().OrderBy(s => s.Id).Skip(500_000).Select(s => s.Code).FirstAsync());
    return await db.Sectors.CountAsync(s => s.Code == code);
}, afficherSql: true);
```
Vérification **côté SQL** : exécutez la requête dans l'outil avec **le plan réel** et cherchez `CONVERT_IMPLICIT` et l'avertissement jaune sur l'opérateur de lecture :

```sql
SET STATISTICS XML ON;     -- ou plan réel dans l'outil graphique ; cherchez PlanAffectingConvert
DECLARE @sku nvarchar(4000) = N'GD-0000424242';
SELECT COUNT(*) FROM dbo.Goods WHERE Sku = @sku;
SET STATISTICS XML OFF;
```

### Exemple avec l'amélioration

Dans votre classe partielle du contexte (`OnModelCreatingPartial`) :

```csharp
partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Good>().Property(g => g.Sku).IsUnicode(false).HasMaxLength(20);   // varchar
    modelBuilder.Entity<Sector>().Property(s => s.Code).IsUnicode(false).HasMaxLength(20);
}
```
(Vérifiez la longueur réelle de la colonne : `SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Goods' AND COLUMN_NAME='Sku';`.)
Relancez les mesures : le paramètre devient `@sku varchar(20)` dans le SQL, sans conversion. Autre option sans toucher au modèle : paramètre typé explicitement avec `FromSql` :

```csharp
var p = new Microsoft.Data.SqlClient.SqlParameter("@sku", System.Data.SqlDbType.VarChar, 20) { Value = "GD-0000424242" };
await Mesurer("FromSql typé", async db => (await db.Goods.FromSqlRaw("SELECT * FROM dbo.Goods WHERE Sku = @sku", p).ToListAsync()).Count);
```
**À observer** : disparition de `CONVERT_IMPLICIT` dans le plan ; *logical reads* et temps avant/après ; **si aucun écart** avec votre collation, notez-le : c'est une information (le risque existe surtout avec des collations Windows).

### Sans index / avec index

`Sku` et `Code` ont déjà des index uniques : c'est précisément ce qui rend la conversion visible (index existant mais non utilisé). Pour une colonne sans index, la conversion est sans effet sur le plan (scan dans tous les cas).

### Comparaison avec une procédure stockée

Les paramètres d'une procédure sont **typés** (`@Sku varchar(20)`) : le problème de conversion n'existe pas pour l'appelant — c'est l'un des vrais bénéfices de typage d'une procédure, que l'on retrouve en LINQ en typant le modèle.

### 🧪 TP 2.10 — « Le plan qui avertit »

*Énoncé.* Sur `Goods.Sku`, reproduisez la recherche d'un SKU avec le modèle d'origine puis avec `IsUnicode(false)`. Récupérez, **dans le plan réel**, la présence de `CONVERT_IMPLICIT`, le type du paramètre, l'opérateur utilisé (Seek/Scan) et les lectures. Concluez pour votre collation.

<details><summary>Correction</summary>

Méthode : exécuter les deux versions, activer le plan réel dans l'outil SQL, comparer. Résultat à reporter dans un tableau (présence de la conversion, opérateur, lectures). Selon la collation et la version, vous verrez soit un plan dégradé (Index Scan + avertissement), soit un Seek dynamique malgré la conversion ; dans les deux cas, la déclaration `IsUnicode(false)` supprime l'ambiguïté et rend le comportement indépendant de la collation.
</details>

---

## 2.11 JSON

**Axe concerné : 🗄️ SQL (lecture de JSON : scan et parsing) + ⚙️ .NET (matérialisation).**

### La notion

EF Core peut mapper une **colonne JSON** (`nvarchar(max)` contenant du JSON) sur un type complexe/possédé (`OwnsOne(...).ToJson()`) et **traduire les requêtes** en `JSON_VALUE` / `OPENJSON`. Intérêt : modèle semi-structuré sans table jointe. **Coût** : chaque ligne est **parsée** ; un filtre sur une propriété JSON est un **scan avec parsing** sauf si l'on **indexe** la propriété via une **colonne calculée** (`JSON_VALUE(...)`).

### Dans quels cas on est concerné

- Attributs variables, documents, « options » stockées en JSON.
- Filtres/tri sur une valeur du JSON.

### Mise en place (objets de démonstration)

```sql
CREATE TABLE dbo.Lab_Doc (Id int NOT NULL PRIMARY KEY, Details nvarchar(max) NOT NULL);
INSERT INTO dbo.Lab_Doc (Id, Details)
SELECT Id, CONCAT('{"Couleur":"', CASE Id % 5 WHEN 0 THEN 'rouge' WHEN 1 THEN 'bleu' WHEN 2 THEN 'vert' WHEN 3 THEN 'noir' ELSE 'blanc' END,
                  '","Poids":', Id % 1000, ',"Tags":["a","b"]}')
FROM dbo.Goods WHERE Id <= 200000;
```
Dans votre projet (types et contexte dédiés) :

```csharp
public class LabDoc { public int Id { get; set; } public DocDetails Details { get; set; } = null!; }
public class DocDetails { public string Couleur { get; set; } = ""; public int Poids { get; set; } public List<string> Tags { get; set; } = []; }

public class LabJsonContext(DbContextOptions<LabJsonContext> o) : DbContext(o)
{
    public DbSet<LabDoc> Docs => Set<LabDoc>();
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<LabDoc>(e => { e.ToTable("Lab_Doc"); e.OwnsOne(x => x.Details, d => { d.ToJson(); d.OwnsMany(y => y.Tags); }); });
}
```
*(La collection primitive `Tags` peut se mapper plus simplement selon la version ; ajustez si votre EF le demande.)*

### Exemple sans l'amélioration

```csharp
var opt = new DbContextOptionsBuilder<LabJsonContext>().UseSqlServer(Cs).LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options;
await using var j = new LabJsonContext(opt);

// ❌ Filtre sur une propriété JSON : JSON_VALUE appliqué à chaque ligne (scan + parsing)
var rouges = await j.Docs.AsNoTracking().CountAsync(d => d.Details.Couleur == "rouge" && d.Details.Poids > 900);

// ❌ Tout rapatrier et filtrer en mémoire
var rouges2 = (await j.Docs.AsNoTracking().ToListAsync()).Count(d => d.Details.Couleur == "rouge" && d.Details.Poids > 900);
```

### Exemple avec l'amélioration

```sql
-- ✔ Indexer la propriété JSON via une colonne calculée
ALTER TABLE dbo.Lab_Doc ADD Couleur AS JSON_VALUE(Details, '$.Couleur');
CREATE INDEX IX_Lab_Doc_Couleur ON dbo.Lab_Doc (Couleur);
```
Relancez le `CountAsync` : si l'expression générée par EF correspond à la définition de la colonne calculée, le plan montre un **seek** sur `IX_Lab_Doc_Couleur` *(à vérifier : l'optimiseur doit reconnaître la même expression `JSON_VALUE(...)`)*.

**À observer** : le SQL généré (`JSON_VALUE([d].[Details], '$.Couleur')` ou `OPENJSON`) ; scan avec calcul dans le plan, puis seek avec la colonne calculée ; mémoire de la version « tout en mémoire ».

### Sans index / avec index

C'est le cœur de la notion : voir ci-dessus (sans / avec colonne calculée indexée).

### Comparaison avec une procédure stockée

Une procédure peut utiliser `OPENJSON ... WITH (...)` pour projeter plusieurs propriétés en une passe ; LINQ en génère un équivalent pour les collections. Pas de différence de plan à même requête.

### 🧪 TP 2.11 — « Filtrer sur le JSON, avec et sans index »

*Énoncé.* Comptez les documents `Couleur = 'rouge'` avec `Poids > 900` : sans index, avec la colonne calculée `Couleur` + index, puis avec une deuxième colonne calculée `Poids` (int) et un index composite. Relevez le SQL, le plan, les lectures.

<details><summary>Correction</summary>

```sql
ALTER TABLE dbo.Lab_Doc ADD Poids AS CAST(JSON_VALUE(Details, '$.Poids') AS int);
CREATE INDEX IX_Lab_Doc_Couleur_Poids ON dbo.Lab_Doc (Couleur, Poids);
```
Attendu : sans index → scan avec `JSON_VALUE` sur 200 000 lignes ; avec l'index composite → seek sur (`Couleur`, `Poids`). Si le plan ne l'utilise pas, c'est que l'expression générée par EF diffère de celle de la colonne calculée : lisez-la dans le SQL du journal et alignez la définition.
</details>

---

## 2.12 Types complexes

**Axe concerné : 🧠 mémoire / ⚙️ .NET (suivi des changements) + lisibilité du SQL.**

### La notion

Un **type complexe** (`ComplexProperty`) regroupe plusieurs colonnes d'une **même table** dans un objet valeur, **sans identité propre** : pas de clé, pas d'entrée séparée dans le change tracker, pas de jointure. À l'inverse, un type **possédé** (`OwnsOne`) est une entité à part entière (même table), avec une entrée de suivi par instance. Le SQL est identique (mêmes colonnes) : la différence est **côté .NET** (nombre d'entrées suivies, mémoire).

### Dans quels cas on est concerné

- Objets valeur (`Adresse`, `Montant`) à grande échelle avec suivi activé.
- Choix entre `OwnsOne`, `ComplexProperty`, et propriétés plates.

### Mise en place

```sql
CREATE TABLE dbo.Lab_Livraison (Id int NOT NULL PRIMARY KEY, Rue nvarchar(60) NOT NULL, Ville nvarchar(40) NOT NULL, Pays nvarchar(20) NOT NULL);
INSERT INTO dbo.Lab_Livraison (Id, Rue, Ville, Pays)
SELECT Id, CONCAT(N'Rue ', Id), CONCAT(N'Ville ', Id % 500), N'FR' FROM dbo.Goods WHERE Id <= 100000;
```
```csharp
public class LivraisonPossedee { public int Id { get; set; } public Adresse Adresse { get; set; } = null!; }
public class LivraisonComplexe { public int Id { get; set; } public Adresse Adresse { get; set; } = null!; }
public class Adresse { public string Rue { get; set; } = ""; public string Ville { get; set; } = ""; public string Pays { get; set; } = ""; }

public class LabCtx(DbContextOptions<LabCtx> o) : DbContext(o)
{
    public DbSet<LivraisonPossedee> Possedees => Set<LivraisonPossedee>();
    public DbSet<LivraisonComplexe> Complexes => Set<LivraisonComplexe>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LivraisonPossedee>(e => { e.ToTable("Lab_Livraison"); e.OwnsOne(x => x.Adresse); });
        b.Entity<LivraisonComplexe>(e => { e.ToTable("Lab_Livraison"); e.ComplexProperty(x => x.Adresse); });
    }
}
```
> Mapper deux entités sur la même table dans **un même** contexte peut poser conflit selon la version : si EF refuse, mettez-les dans **deux** contextes distincts.

### Exemple sans l'amélioration

```csharp
// ❌ OwnsOne + suivi : une entrée de suivi pour l'entité ET une pour l'objet possédé
await using var c1 = new LabCtx(new DbContextOptionsBuilder<LabCtx>().UseSqlServer(Cs).Options);
var l1 = await c1.Possedees.ToListAsync();
Console.WriteLine($"OwnsOne : {c1.ChangeTracker.Entries().Count():N0} entrées suivies pour {l1.Count:N0} lignes");
```

### Exemple avec l'amélioration

```csharp
// ✔ ComplexProperty : l'adresse fait partie de l'entrée de l'entité
await using var c2 = new LabCtx(new DbContextOptionsBuilder<LabCtx>().UseSqlServer(Cs).Options);
var l2 = await c2.Complexes.ToListAsync();
Console.WriteLine($"Complex : {c2.ChangeTracker.Entries().Count():N0} entrées suivies pour {l2.Count:N0} lignes");
```
**À observer** : le nombre d'entrées suivies (≈ 2× contre ≈ 1×) ; la mémoire (`Mesurer` autour de chaque `ToListAsync`) ; le SQL **identique** (mêmes colonnes) ; `ChangeTracker.DebugView.ShortView` pour voir la structure du suivi.

### Sans index / avec index et procédure stockée

Sans objet : c'est une question de modèle .NET.

### 🧪 TP 2.12 — « Combien d'objets suivis ? »

*Énoncé.* Chargez les 100 000 livraisons en `OwnsOne`, puis en `ComplexProperty`, avec et sans `AsNoTracking`. Remplissez le tableau « entrées suivies / Mo alloués / temps » et dites quand le type complexe est préférable.

<details><summary>Correction</summary>

Avec `AsNoTracking`, le coût de suivi disparaît dans les deux cas et les écarts se réduisent à la matérialisation. Le type complexe est préférable quand l'objet est une **valeur** sans identité (adresse, montant) et que le suivi est actif ; `OwnsOne` reste pertinent quand l'objet a un cycle de vie ou des relations propres ou quand il faut le mapper en JSON (2.11).
</details>

---

## 2.13 Fonctions de fenêtrage

**Axe concerné : 🗄️ SQL — c'est une des raisons légitimes de quitter LINQ.**

### La notion

Les fonctions de fenêtrage (`ROW_NUMBER`, `RANK`, `LAG`, `SUM() OVER (PARTITION BY …)`) n'ont **pas d'équivalent LINQ standard**. Pour le motif le plus fréquent — le **top N par groupe** — LINQ propose des contournements : sous-requête corrélée avec `Take(n)` sur une navigation (traduite en `CROSS APPLY` ou en sous-requête), `GroupBy` + `OrderBy…First()` pour N = 1. Pour le reste : `FromSql` avec `ROW_NUMBER()` (le résultat reste composable si c'est une sélection).

### Dans quels cas on est concerné

- Top N par groupe, classements, différences avec la ligne précédente (`LAG`), cumuls glissants.
- Dédoublonnage « garder la dernière ligne par clé ».

### Exemple sans l'amélioration

```csharp
// ❌ Les 3 plus gros échanges de chacun des 100 premiers biens : N+1
await Mesurer("AVANT : N+1", async db =>
{
    var n = 0;
    foreach (var id in Enumerable.Range(1, 100))
        n += (await db.Trades.AsNoTracking().Where(t => t.GoodId == id).OrderByDescending(t => t.UnitPrice).Take(3).ToListAsync()).Count;
    return n;
});

// ❌ Tout rapatrier puis grouper
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.GoodId <= 100).ToListAsync();
    return l.GroupBy(t => t.GoodId).SelectMany(g => g.OrderByDescending(t => t.UnitPrice).Take(3)).Count();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Sous-requête corrélée sur la navigation : une requête (CROSS APPLY / sous-requête)
await Mesurer("APRÈS (1) : navigation + Take(3)", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 100)
        .Select(g => new { g.Id, Top = g.Trades.OrderByDescending(t => t.UnitPrice).Take(3).Select(t => t.UnitPrice).ToList() })
        .ToListAsync()).Sum(x => x.Top.Count), afficherSql: true);

// ✔ ROW_NUMBER() via FromSql, composable ensuite
await Mesurer("APRÈS (2) : FromSql ROW_NUMBER", async db =>
    (await db.Trades.FromSql($"""
        SELECT * FROM (
          SELECT *, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) AS rn
          FROM dbo.Trades WHERE GoodId <= 100) x
        WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);
```
**À observer** : le SQL de la navigation (`OUTER/CROSS APPLY` avec `TOP`) ; les plans des deux formes ; la limite des écritures LINQ (`GroupBy` + `SelectMany(Take)` peut ne pas être traduit selon la version : lisez le message d'EF).

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
-- sans index sur (GoodId, UnitPrice) : tri par partition à partir de IX_Trades_GoodId + lookups
CREATE INDEX IX_Lab_Trades_Good_Price ON dbo.Trades (GoodId, UnitPrice DESC) INCLUDE (MarketId, CompanyId, TradeType, Quantity, TradedAt);
-- avec index : les lignes sont déjà dans l'ordre de la partition ; plus de Sort ni de lookup
DROP INDEX IX_Lab_Trades_Good_Price ON dbo.Trades;
```
Relancez les deux formes C# avant/après.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_TopEchangesParBien @GoodMax int, @N int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, MarketId, CompanyId, GoodId, TradeType, Quantity, UnitPrice, TradedAt
    FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY GoodId ORDER BY UnitPrice DESC) AS rn
          FROM dbo.Trades WHERE GoodId <= @GoodMax) x
    WHERE rn <= @N;
END
```
```csharp
await Mesurer("PROC : top N par bien", async db =>
    (await db.Trades.FromSql($"EXEC dbo.Lab_TopEchangesParBien {100}, {3}").AsNoTracking().ToListAsync()).Count);
```
**À observer** : même plan que le `FromSql` ci-dessus. La procédure apporte la stabilité du texte SQL et l'isolation du SQL de l'application, **pas** de vitesse ; la forme LINQ par navigation, elle, reste composable (on peut ajouter un `Where`) tandis que l'`EXEC` ne l'est pas.

### 🧪 TP 2.13 — « Le dernier échange de chaque société »

*Énoncé.* Pour les 1 000 premières sociétés, retournez leur échange le plus récent. Écrivez-le (a) avec `GroupBy` + `OrderByDescending().First()`, (b) avec une navigation et `FirstOrDefault`, (c) avec `ROW_NUMBER()` en `FromSql`. Comparez SQL et plans, avec et sans l'index `(CompanyId, TradedAt DESC)`.

<details><summary>Correction</summary>

```csharp
// (b) navigation
db.Companies.Where(c => c.Id <= 1_000)
  .Select(c => c.Trades.OrderByDescending(t => t.TradedAt).Select(t => new { t.CompanyId, t.TradedAt, t.UnitPrice }).FirstOrDefault())
// (c)
db.Trades.FromSql($"""
  SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CompanyId ORDER BY TradedAt DESC) rn FROM dbo.Trades WHERE CompanyId <= 1000) x WHERE rn = 1
  """)
```
```sql
CREATE INDEX IX_Lab_Trades_Company_Date ON dbo.Trades (CompanyId, TradedAt DESC);
-- (b) devient un « Top 1 par société » avec un seek par société ; (c) lit l'index dans l'ordre sans Sort.
DROP INDEX IX_Lab_Trades_Company_Date ON dbo.Trades;
```
Sans index les trois formes trient ou rejouent un tri par société ; avec index la différence de **forme** s'efface au profit de la **structure de l'index**.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Goods_Name                ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Goods_BasePrice           ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Goods_NomInverse          ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Planets_Population        ON dbo.Planets;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt           ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Good_Price         ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Company_Date       ON dbo.Trades;
IF COL_LENGTH('dbo.Goods', 'NomInverse') IS NOT NULL ALTER TABLE dbo.Goods DROP COLUMN NomInverse;

DROP TABLE IF EXISTS dbo.Lab_Doc;
DROP TABLE IF EXISTS dbo.Lab_Livraison;

DROP FUNCTION  IF EXISTS dbo.Lab_PrixTTC;
DROP FUNCTION  IF EXISTS dbo.Lab_EchangesDuBien;
DROP PROCEDURE IF EXISTS dbo.Lab_TopEchangesParBien;

-- Si le plein texte a été créé :
-- DROP FULLTEXT INDEX ON dbo.Goods; DROP FULLTEXT CATALOG LabCat;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
