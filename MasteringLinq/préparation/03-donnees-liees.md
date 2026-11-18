# 03 — Chargement des données liées

> Suite de [`02-traduction.md`](02-traduction.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans), complétée ci-dessous d'une mesure du **volume reçu par le réseau**.
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**. Les points dépendant de la version d'EF Core sont marqués *(à vérifier)*.
> Index de démonstration : `IX_Lab_` ; objets SQL : `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

- [Outil complémentaire : mesurer les octets reçus](#outil-complémentaire--mesurer-les-octets-reçus)
- [3.1 `Include` / `ThenInclude` (chargement « eager »)](#31-include--theninclude)
- [3.2 Requêtes fractionnées (`AsSplitQuery`)](#32-requêtes-fractionnées-assplitquery)
- [3.3 Projection (`Select`)](#33-projection-select)
- [3.4 Chargement explicite](#34-chargement-explicite)
- [3.5 Lazy loading (proxies)](#35-lazy-loading-proxies)
- [3.6 `Include` filtré](#36-include-filtré)
- [3.7 Explosion cartésienne](#37-explosion-cartésienne)
- [3.8 `AsSplitQuery` / `AsSingleQuery` : réglage global ou local](#38-assplitquery--assinglequery--global-ou-local)
- [3.9 Cohérence des requêtes fractionnées](#39-cohérence-des-requêtes-fractionnées)
- [3.10 Le problème N+1 : détecter et corriger](#310-le-problème-n1)
- [3.11 `AutoInclude` / `IgnoreAutoIncludes`](#311-autoinclude--ignoreautoincludes)
- [3.12 Filtres globaux (`HasQueryFilter`)](#312-filtres-globaux)
- [3.13 *Fixup* : relier les entités déjà chargées](#313-fixup)
- [Script de nettoyage](#script-de-nettoyage)

Relations utilisées : `Sector → StarSystems → Planets → (Inhabitants, Markets)` ; `Market → (MarketGoods, Trades, StarshipCargos)` ; `Good → (Trades, MarketGoods, StarshipCargos)` ; `Company → (Starships, Trades)`. **Fan-out moyens** : un bien a ~6 `Trades`, ~4 `MarketGoods`, ~3 `StarshipCargos` ; une société ~2 `Starships`, ~6 `Trades` ; une planète ~1,3 habitant, ~0,7 marché.

## Outil complémentaire : mesurer les octets reçus

À ajouter à votre `Program.cs` (nécessite `using Microsoft.Data.SqlClient;`). Il ouvre la connexion à la main pour lire les statistiques d'ADO.NET :

```csharp
async Task<T> MesurerReseau<T>(string nom, Func<StarWarsContext, Task<T>> action)
{
    await using var db = NewDb();
    await db.Database.OpenConnectionAsync();
    var cn = (SqlConnection)db.Database.GetDbConnection();
    cn.StatisticsEnabled = true; cn.ResetStatistics();
    compteur.Reset();
    var sw = Stopwatch.StartNew();
    var r = await action(db);
    sw.Stop();
    var s = cn.RetrieveStatistics();
    Console.WriteLine($"{nom,-50} {sw.ElapsedMilliseconds,7:N0} ms | SQL: {compteur.Requetes} req, {compteur.Lignes:N0} lignes |" +
                      $" {Convert.ToInt64(s["BytesReceived"]) / 1_048_576.0,7:N1} Mo reçus | {r}");
    return r;
}
```
`BytesReceived` = volume réellement transféré du serveur au client : le meilleur indicateur de l'**explosion cartésienne**.

Pour voir les **avertissements** d'EF (ex. `MultipleCollectionIncludeWarning`), ajoutez dans `NewDb` : `.LogTo(Console.WriteLine, new[] { DbLoggerCategory.Query.Name }, LogLevel.Warning)`.

---

## 3.1 `Include` / `ThenInclude`

**Axe concerné : 🗄️ SQL (jointures, colonnes dupliquées) + 🧠 mémoire (suivi) + ⚙️ .NET.**

### La notion

`Include` charge une navigation avec l'entité principale, en **une requête** (`LEFT JOIN`), par défaut. Pour une navigation **collection**, le parent est **répété sur chaque ligne enfant** : plus l'arbre est profond ou large, plus le résultat gonfle. `Include` charge **toutes les colonnes** de toutes les entités et les **suit** (tracking).

### Dans quels cas on est concerné

- Vous avez besoin des **entités** (pour les modifier) avec leurs enfants.
- Vous n'avez besoin que de **quelques champs** : `Include` est alors un gaspillage ; préférer la projection (3.3).

### Exemple sans l'amélioration

```csharp
// ❌ Include sur 3 niveaux alors que seuls les noms sont affichés
await MesurerReseau("AVANT : Include ×3 niveaux", async db =>
    (await db.Sectors.Where(s => s.Id <= 50)
        .Include(s => s.StarSystems).ThenInclude(ss => ss.Planets).ThenInclude(p => p.Markets)
        .ToListAsync()).Count);
```

### Exemple avec l'amélioration

```csharp
// ✔ Include justifié : on va modifier ces entités → tracking nécessaire, mais on limite la profondeur
await MesurerReseau("APRÈS : Include sur 1 niveau", async db =>
    (await db.Planets.Where(p => p.Id <= 5_000).Include(p => p.Markets).ToListAsync()).Count);

// ✔ Lecture seule : sans tracking
await MesurerReseau("APRÈS : Include + AsNoTracking", async db =>
    (await db.Planets.AsNoTracking().Where(p => p.Id <= 5_000).Include(p => p.Markets).ToListAsync()).Count);
```

**À observer** : le SQL (`LEFT JOIN`s imbriqués) ; **lignes lues** (au moins une par enfant) ; Mo reçus ; la mémoire ; le nombre d'entités suivies (`db.ChangeTracker.Entries().Count()`).

### Sans index / avec index

Les `JOIN` s'appuient sur les index des clés étrangères (`IX_StarSystems_SectorId`, `IX_Planets_StarSystemId`, `IX_Markets_PlanetId`) : **ils existent déjà**. Pour voir l'effet de leur absence, créez temporairement une variante sans index sur une copie ou comparez avec un `Include` sur une relation dont la clé étrangère n'est **pas** indexée dans votre modèle (aucune ici : c'est la convention d'EF). À retenir : une clé étrangère **sans index** transforme chaque `Include` en scan.

### Comparaison avec une procédure stockée

Une procédure qui renvoie plusieurs jeux de résultats (parent, enfants) équivaut à une **requête fractionnée** ; EF Core ne lit qu'un seul jeu : il faut passer par ADO.NET (chapitre 10).

### 🧪 TP 3.1 — « Le bon périmètre d'`Include` »

*Énoncé.* Affichez le nom de chaque secteur d'`Id ≤ 50` avec le nombre de ses planètes. (a) avec `Include(StarSystems).ThenInclude(Planets)` puis comptage en mémoire, (b) en `Select` avec `Count()`. Comparez requêtes, lignes, Mo reçus, mémoire.

<details><summary>Correction</summary>

```csharp
await MesurerReseau("TP (a)", async db =>
{
    var l = await db.Sectors.AsNoTracking().Where(s => s.Id <= 50).Include(s => s.StarSystems).ThenInclude(ss => ss.Planets).ToListAsync();
    return l.Select(s => (s.Name, N: s.StarSystems.Sum(ss => ss.Planets.Count))).Count();
});
await MesurerReseau("TP (b)", async db =>
    (await db.Sectors.Where(s => s.Id <= 50)
        .Select(s => new { s.Name, N = s.StarSystems.SelectMany(ss => ss.Planets).Count() }).ToListAsync()).Count);
```
(b) ne ramène que 2 colonnes par secteur ; les dizaines de milliers de lignes de planètes de (a) n'existent plus côté client. `Include` se justifie **uniquement** si l'on utilise les entités liées.
</details>

---

## 3.2 Requêtes fractionnées (`AsSplitQuery`)

**Axe concerné : 🗄️ SQL (nombre de requêtes vs volume) + 🧠 mémoire.**

### La notion

`AsSplitQuery()` remplace le `LEFT JOIN` unique par **une requête par collection incluse** : une pour le parent, puis une par collection (qui rejoint de nouveau le parent). Les lignes du parent ne sont plus répétées pour chaque enfant ; on **échange** des allers-retours supplémentaires contre un **volume bien moindre** quand plusieurs collections sont incluses. Contrepartie : les requêtes ne sont **pas atomiques** (3.9) et le parent est lu plusieurs fois.

### Dans quels cas on est concerné

- **Plusieurs** `Include` de collections sur la même entité (explosion cartésienne, 3.7).
- Collections volumineuses, ou entités parentes larges (nombreuses colonnes, texte long).
- Pas dans le cas d'une seule collection peu volumineuse : la requête unique est meilleure.

### Exemple sans l'amélioration

```csharp
// ❌ Deux collections incluses en une seule requête : produit cartésien (voir 3.7)
await MesurerReseau("AVANT : single query, 2 collections", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000)
        .Include(g => g.Trades).Include(g => g.MarketGoods).ToListAsync()).Count);
```

### Exemple avec l'amélioration

```csharp
await MesurerReseau("APRÈS : AsSplitQuery", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000)
        .Include(g => g.Trades).Include(g => g.MarketGoods).AsSplitQuery().ToListAsync()).Count);
```
**À observer** : 1 requête → 3 ; **lignes lues** ≈ 2 000 × 6 × 4 = 48 000 → 2 000 + 12 000 + 8 000 = 22 000 ; Mo reçus ; l'**avertissement** `MultipleCollectionIncludeWarning` dans la version « single » si vous n'avez fixé aucun comportement par défaut ; dans le SQL de split, le `ORDER BY` sur les clés (nécessaire pour rapprocher les lignes).

### Sans index / avec index

Chaque requête de split rejoint le parent : les index de clés étrangères (`IX_Trades_GoodId`, `IX_MarketGoods_GoodId`) servent. Sur une FK **non** indexée, chaque requête fractionnée scanne la table enfant : le split multiplie alors le problème par le nombre de collections.

### Comparaison avec une procédure stockée

Une procédure renvoyant trois jeux de résultats est l'équivalent exact du split ; EF Core ne la consomme pas nativement (chapitre 10).

### 🧪 TP 3.2 — « Quand le split ne vaut rien »

*Énoncé.* Sur `Companies` d'`Id ≤ 2 000` avec `Include(Starships)` (une seule collection) : comparez *single* et *split*. Puis avec `Include(Starships).Include(Trades)`. À partir de quel moment le split devient-il utile ? Vérifiez avec les Mo reçus et le nombre de lignes.

<details><summary>Correction</summary>

Avec **une** collection, le split n'économise rien (les lignes du parent ne sont répétées qu'une fois par enfant) et **ajoute** un aller-retour. Avec **deux** collections, la requête unique renvoie ≈ (starships × trades) lignes par société (~12) contre (starships + trades) (~8) en split : l'avantage croît avec le produit des cardinalités. La règle pratique : le split est utile dès **deux collections** ou dès qu'une collection est très volumineuse.
</details>

---

## 3.3 Projection (`Select`)

**Axe concerné : 🗄️ SQL (colonnes, lignes) + 🧠 mémoire + ⚙️ .NET.**

### La notion

Un `Select` vers un type anonyme/DTO ne ramène que les **colonnes demandées**, sans suivi, avec des **sous-collections** traduites en jointure ou sous-requête. C'est généralement **la meilleure stratégie de lecture** : pas de duplication des colonnes du parent, pas de suivi, un index couvrant possible.

### Dans quels cas on est concerné

- Tout affichage ou export en **lecture seule**.
- Quand l'objet affiché n'est pas une entité du modèle (vue, tableau, API).

### Exemple sans l'amélioration

```csharp
// ❌ Entités complètes + Include pour afficher 3 champs
await MesurerReseau("AVANT : Include", async db =>
    (await db.Planets.Where(p => p.Id <= 10_000).Include(p => p.Markets).ToListAsync())
        .Select(p => new { p.Name, Marches = p.Markets.Select(m => m.Name).ToList() }).Count());
```

### Exemple avec l'amélioration

```csharp
// ✔ La projection fait le travail dans SQL
await MesurerReseau("APRÈS : Select", async db =>
    (await db.Planets.Where(p => p.Id <= 10_000)
        .Select(p => new { p.Name, Marches = p.Markets.Select(m => m.Name).ToList() }).ToListAsync()).Count);
```
**À observer** : colonnes dans le `SELECT` ; Mo reçus ; absence de suivi.

### Sans index / avec index

Index **couvrant** : la projection permet à SQL Server de ne lire que l'index.

```sql
SET STATISTICS IO, TIME ON;
SELECT p.Name, m.Name FROM dbo.Planets p JOIN dbo.Markets m ON m.PlanetId = p.Id WHERE p.Id <= 10000;    -- lookups sur Markets
GO
CREATE INDEX IX_Lab_Markets_Planet_Name ON dbo.Markets (PlanetId) INCLUDE (Name);
GO
SELECT p.Name, m.Name FROM dbo.Planets p JOIN dbo.Markets m ON m.PlanetId = p.Id WHERE p.Id <= 10000;    -- lecture de l'index seul
GO
DROP INDEX IX_Lab_Markets_Planet_Name ON dbo.Markets;
```
L'**entité complète** (`Include`) ne profiterait pas de cet index : il lui faudrait toutes les colonnes.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_PlanetesEtMarches @PlanetMax int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.Id AS PlaneteId, p.Name AS Planete, m.Name AS Marche
    FROM dbo.Planets p LEFT JOIN dbo.Markets m ON m.PlanetId = p.Id
    WHERE p.Id <= @PlanetMax;
END
```
```csharp
public record PlaneteEtMarche(int PlaneteId, string Planete, string? Marche);
await MesurerReseau("PROC : planètes + marchés", async db =>
    (await db.Database.SqlQuery<PlaneteEtMarche>($"EXEC dbo.Lab_PlanetesEtMarches {10_000}").ToListAsync()).Count);
```
**À observer** : le résultat est **à plat** (une ligne par couple) et il faut regrouper en C# si l'on veut la forme parent/enfants ; le plan est celui du `LEFT JOIN`. LINQ avec sous-collection fait le regroupement pour vous.

### 🧪 TP 3.3 — « Tableau planète / habitants / marchés »

*Énoncé.* Pour les planètes d'`Id ≤ 5 000`, affichez : nom, nombre d'habitants, nombre de marchés noirs. Écrivez-le en projection (une requête) et comparez avec `Include` des deux collections. Notez requêtes, lignes, Mo reçus.

<details><summary>Correction</summary>

```csharp
await MesurerReseau("TP projection", async db =>
    (await db.Planets.Where(p => p.Id <= 5_000)
        .Select(p => new { p.Name, Habitants = p.Inhabitants.Count(), Noirs = p.Markets.Count(m => m.IsBlackMarket) }).ToListAsync()).Count);

await MesurerReseau("TP Include ×2", async db =>
    (await db.Planets.AsNoTracking().Where(p => p.Id <= 5_000).Include(p => p.Inhabitants).Include(p => p.Markets).ToListAsync())
        .Select(p => (p.Name, p.Inhabitants.Count, p.Markets.Count(m => m.IsBlackMarket))).Count());
```
La projection renvoie une ligne par planète avec deux entiers (sous-requêtes ou agrégats) ; `Include ×2` renvoie toutes les colonnes de tous les habitants et marchés (et, en *single query*, leur produit).
</details>

---

## 3.4 Chargement explicite

**Axe concerné : 🗄️ SQL (allers-retours) + ⚙️ .NET.**

### La notion

`db.Entry(entité).Collection(e => e.Enfants).Load()` (ou `.Reference(...)`) charge une navigation **à la demande**, pour une entité **déjà suivie**. `.Query()` permet de **filtrer** ou **compter** sans charger : `Collection(...).Query().CountAsync()`. C'est utile pour un chargement **conditionnel** ; en boucle, c'est un **N+1** (3.10).

### Dans quels cas on est concerné

- Charger les enfants **seulement** pour certains parents (selon une règle métier).
- Obtenir un compteur ou un agrégat sans matérialiser la collection.

### Exemple sans l'amélioration

```csharp
// ❌ Chargement explicite dans une boucle : 1 requête par planète
await Mesurer("AVANT : Load() en boucle", async db =>
{
    var planetes = await db.Planets.Where(p => p.Id <= 2_000).ToListAsync();
    foreach (var p in planetes) await db.Entry(p).Collection(x => x.Markets).LoadAsync();
    return planetes.Sum(p => p.Markets.Count);
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Compter sans charger
await Mesurer("APRÈS : Query().CountAsync()", async db =>
{
    var p = await db.Planets.FirstAsync(x => x.Id == 1_000);
    return await db.Entry(p).Collection(x => x.Markets).Query().CountAsync();      // SELECT COUNT(*), rien n'est matérialisé
}, afficherSql: true);

// ✔ Charger seulement quand c'est utile
await Mesurer("APRÈS : chargement conditionnel", async db =>
{
    var planetes = await db.Planets.Where(p => p.Id <= 2_000).ToListAsync();
    foreach (var p in planetes.Where(p => p.HasSpaceport))                           // seules les planètes avec spatioport
        await db.Entry(p).Collection(x => x.Markets).LoadAsync();
    return planetes.Sum(p => p.Markets.Count);
});

// ✔ Un seul chargement pour toutes (fixup, voir 3.13)
await Mesurer("APRÈS : 1 requête pour tous les enfants", async db =>
{
    var planetes = await db.Planets.Where(p => p.Id <= 2_000).ToListAsync();
    await db.Markets.Where(m => m.PlanetId <= 2_000).LoadAsync();                    // relie les marchés aux planètes déjà suivies
    return planetes.Sum(p => p.Markets.Count);
});
```
**À observer** : le nombre de requêtes (≈ 2 001 → 2) ; le `COUNT(*)` du `Query().CountAsync()`.

### Sans index / avec index

Chaque `Load()` filtre par `PlanetId` : l'index de FK `IX_Markets_PlanetId` permet un seek par planète. Sans lui, chaque chargement scanne `Markets` (2 M).

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 3.4 — « Charger uniquement ce qui sert »

*Énoncé.* Pour les planètes d'`Id ≤ 2 000`, affichez le nom et le nombre de marchés **pour celles qui ont un spatioport**, et chargez la liste complète des marchés **uniquement** si le nombre est supérieur à 1. Écrivez une version naïve (`Load()` pour toutes) et une version optimisée.

<details><summary>Correction</summary>

```csharp
await Mesurer("TP optimisé", async db =>
{
    var planetes = await db.Planets.Where(p => p.Id <= 2_000 && p.HasSpaceport).ToListAsync();
    var n = 0;
    foreach (var p in planetes)
    {
        var nb = await db.Entry(p).Collection(x => x.Markets).Query().CountAsync();        // 1 COUNT par planète…
        if (nb > 1) await db.Entry(p).Collection(x => x.Markets).LoadAsync();
        n += nb;
    }
    return n;
});
```
Mieux : un seul `GroupBy` côté SQL (`Select(p => new { p.Id, N = p.Markets.Count() })`) puis un seul chargement groupé pour les planètes concernées. Le chargement explicite par élément reste un N+1 : à réserver aux cas **rares**.
</details>

---

## 3.5 Lazy loading (proxies)

**Axe concerné : 🗄️ SQL (allers-retours) — à proscrire à grande échelle.**

### La notion

Avec les **proxies de chargement différé** (`UseLazyLoadingProxies()`, navigations `virtual`), accéder à `planete.Markets` déclenche **automatiquement** une requête si la collection n'est pas chargée. Pratique, mais **invisible** : une boucle `foreach (var p in planetes) … p.Markets …` produit **une requête par planète** (N+1).

### Dans quels cas on est concerné

- Code existant qui parcourt des graphes d'objets.
- Rendu de vues, sérialisation (`System.Text.Json` parcourt toutes les navigations).

### Mise en place (optionnelle, à faire chez vous)

```bash
dotnet add package Microsoft.EntityFrameworkCore.Proxies
```
Dans `NewDb` : `.UseLazyLoadingProxies()` (les navigations du modèle généré sont déjà `virtual`). **Pour ne pas fausser les autres essais**, utilisez une fabrique dédiée `NewDbLazy()`.

### Exemple sans l'amélioration

```csharp
// ❌ Lazy loading : le code ne montre rien, la base reçoit ≈ 2 001 requêtes
await Mesurer("AVANT : lazy loading", async db =>      // db créé par NewDbLazy() dans votre version
{
    var planetes = await db.Planets.Where(p => p.Id <= 2_000).ToListAsync();
    return planetes.Sum(p => p.Markets.Count);          // déclenche une requête par planète
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Sans proxies, avec Include (ou projection)
await Mesurer("APRÈS : Include", async db =>
    (await db.Planets.Where(p => p.Id <= 2_000).Include(p => p.Markets).ToListAsync()).Sum(p => p.Markets.Count));

await Mesurer("APRÈS : projection", async db =>
    (await db.Planets.Where(p => p.Id <= 2_000).Select(p => p.Markets.Count).ToListAsync()).Sum());
```
**À observer** : le compteur de requêtes (≈ 2 001 contre 1) ; le temps ; en production, le symptôme est une liste de milliers de petites requêtes identiques dans Query Store.

### Sans index / avec index

Même remarque qu'en 3.4 : l'index de FK rend chaque requête rapide ; il ne supprime pas le **nombre** de requêtes.

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 3.5 — « Chasser le lazy loading »

*Énoncé.* Écrivez une méthode qui calcule le nombre total d'habitants des 1 000 premières planètes avec le lazy loading, comptez les requêtes, puis corrigez-la **sans changer sa signature** pour qu'elle n'en émette qu'une.

<details><summary>Correction</summary>

```csharp
// naïf (avec proxies) : 1 + 1000 requêtes
static async Task<int> HabitantsLazy(StarWarsContext db) =>
    (await db.Planets.Where(p => p.Id <= 1_000).ToListAsync()).Sum(p => p.Inhabitants.Count);
// corrigé : une requête, sans entité du tout
static Task<int> HabitantsOk(StarWarsContext db) =>
    db.Planets.Where(p => p.Id <= 1_000).SumAsync(p => p.Inhabitants.Count());
```
Le compteur de requêtes de `Mesurer` donne 1 001 contre 1. Désactiver les proxies (ou ne pas les activer) transforme cette erreur silencieuse en `NullReferenceException`/collection vide **visible** au test : c'est un avantage.
</details>

---

## 3.6 `Include` filtré

**Axe concerné : 🗄️ SQL (lignes enfants ramenées) + 🧠 mémoire.**

### La notion

`Include(m => m.Trades.Where(...).OrderByDescending(...).Take(5))` charge **seulement une partie** de la collection. Cela évite de ramener des milliers d'enfants pour n'en garder que quelques-uns. Limites : seuls `Where`, `OrderBy`/`ThenBy`, `Skip`, `Take` sont autorisés dans l'`Include` ; le `Take` par parent se traduit par un `ROW_NUMBER` ou une sous-requête corrélée selon la version.

### Dans quels cas on est concerné

- « Les N derniers éléments de chaque parent ».
- Collections très volumineuses (échanges d'un marché) dont on n'affiche qu'un extrait.

### Exemple sans l'amélioration

```csharp
// ❌ Tous les échanges des marchés, puis tri/extrait en mémoire
await MesurerReseau("AVANT : Include complet", async db =>
{
    var l = await db.Markets.AsNoTracking().Where(m => m.Id <= 2_000).Include(m => m.Trades).ToListAsync();
    return l.Sum(m => m.Trades.OrderByDescending(t => t.TradedAt).Take(5).Count());
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Les 5 derniers échanges de chaque marché, filtrés dans SQL
await MesurerReseau("APRÈS : Include filtré", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Id <= 2_000)
        .Include(m => m.Trades.OrderByDescending(t => t.TradedAt).Take(5)).ToListAsync()).Sum(m => m.Trades.Count));
```

### Sans index / avec index

Le « top 5 par parent » trie les échanges **de chaque marché** : un index `(MarketId, TradedAt DESC)` supprime le tri.

```sql
CREATE INDEX IX_Lab_Trades_Market_Date ON dbo.Trades (MarketId, TradedAt DESC);
-- relancer le cas C# avant/après : plan sans Sort, seek par marché
DROP INDEX IX_Lab_Trades_Market_Date ON dbo.Trades;
```

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_DerniersEchangesParMarche @MarcheMax int, @N int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, MarketId, CompanyId, GoodId, TradeType, Quantity, UnitPrice, TradedAt
    FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY MarketId ORDER BY TradedAt DESC) AS rn
          FROM dbo.Trades WHERE MarketId <= @MarcheMax) x
    WHERE rn <= @N;
END
```
```csharp
await Mesurer("PROC : derniers échanges", async db =>
    (await db.Trades.FromSql($"EXEC dbo.Lab_DerniersEchangesParMarche {2_000}, {5}").AsNoTracking().ToListAsync()).Count);
```
**À observer** : même plan que l'`Include` filtré une fois l'index créé ; la procédure renvoie des `Trade` à plat (pas de `Market` associé).

### 🧪 TP 3.6 — « Les 3 meilleurs prix par bien »

*Énoncé.* Pour les biens d'`Id ≤ 1 000`, chargez les 3 échanges de plus fort `UnitPrice` de chacun avec un `Include` filtré. Comparez avec un `Include` complet suivi d'un tri en mémoire. Index conseillé ?

<details><summary>Correction</summary>

```csharp
.Include(g => g.Trades.OrderByDescending(t => t.UnitPrice).Take(3))
```
Index : `(GoodId, UnitPrice DESC)` (`IX_Lab_Trades_Good_Price`, voir 2.13). Sans lui, l'`Include` complet ramène ~6 000 lignes pour en garder 3 000 ; l'`Include` filtré en renvoie 3 000 mais doit trier par bien.
</details>

---

## 3.7 Explosion cartésienne

**Axe concerné : 🗄️ SQL (volume transféré) + 🧠 mémoire — c'est le piège n° 1 des `Include`.**

### La notion

Quand une requête unique inclut **deux collections ou plus** du même parent, chaque combinaison enfant A × enfant B devient **une ligne** : le résultat est le **produit** des cardinalités, et le parent (toutes ses colonnes) est répété sur chacune. Pour un bien avec 6 échanges, 4 `MarketGoods`, 3 `StarshipCargos` : **72 lignes** au lieu de 13. EF le signale (`MultipleCollectionIncludeWarning`).

### Dans quels cas on est concerné

- Plusieurs `Include` de collections sur la même entité.
- Entités parentes **larges** (colonnes volumineuses) : la répétition coûte cher.

### Exemple sans l'amélioration

```csharp
// ❌ Trois collections : produit cartésien
await MesurerReseau("AVANT : single query 3 collections", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000)
        .AsSingleQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);
```

### Exemple avec l'amélioration

```csharp
// ✔ Split query
await MesurerReseau("APRÈS (1) : AsSplitQuery", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000)
        .AsSplitQuery()
        .Include(g => g.Trades).Include(g => g.MarketGoods).Include(g => g.StarshipCargos).ToListAsync()).Count);

// ✔ Projection : seulement ce qu'on affiche
await MesurerReseau("APRÈS (2) : projection", async db =>
    (await db.Goods.Where(g => g.Id <= 2_000)
        .Select(g => new { g.Sku, Echanges = g.Trades.Count(), Marches = g.MarketGoods.Count(), Cargos = g.StarshipCargos.Count() })
        .ToListAsync()).Count);
```
**À observer** : **lignes lues** (≈ 2 000 × 72 = 144 000 contre ≈ 2 000 × 13 = 26 000 en split, et 2 000 en projection) ; **Mo reçus** ; mémoire ; l'avertissement du journal ; dans le SQL « single », les trois `LEFT JOIN`.

### Sans index / avec index

Sans objet : le problème est le **volume**, pas la recherche.

### Comparaison avec une procédure stockée

La procédure qui renvoie les trois collections en **trois jeux de résultats** évite le produit (comme le split) ; en un seul jeu, elle reproduirait le piège.

### 🧪 TP 3.7 — « Mesurer l'explosion »

*Énoncé.* Pour `Goods` d'`Id ≤ 2 000`, faites varier le nombre de collections incluses (1, 2, 3) en *single query* puis en *split*. Remplissez le tableau « collections / lignes lues / Mo reçus / temps » dans les deux modes et tracez la tendance.

<details><summary>Correction</summary>

Méthode : six exécutions de `MesurerReseau`, une par couple (nombre de collections, mode). Attendu : en *single*, le nombre de lignes suit le **produit** des cardinalités (≈ 6, ≈ 24, ≈ 72 par bien) ; en *split*, la **somme** (≈ 6, ≈ 10, ≈ 13). Les Mo reçus suivent la même courbe, aggravée par la largeur des colonnes du parent répétées.
</details>

---

## 3.8 `AsSplitQuery` / `AsSingleQuery` : global ou local

**Axe concerné : 🗄️ SQL + configuration.**

### La notion

Le comportement par défaut est **une requête unique** (*single query*). On peut :
- changer le **défaut du contexte** : `UseSqlServer(Cs, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))` ;
- forcer **par requête** : `.AsSplitQuery()` ou `.AsSingleQuery()`.

Quand le défaut est *split*, `AsSingleQuery()` est la dérogation (par exemple pour la cohérence ou pour un `Include` d'une seule petite collection). Sans défaut explicite et avec plusieurs collections, EF émet l'avertissement de 3.7.

### Dans quels cas on est concerné

- Base de code où presque toutes les requêtes incluent plusieurs collections : régler le défaut.
- Requêtes où l'atomicité compte : `AsSingleQuery()` ponctuel.

### Exemple sans l'amélioration

```csharp
// ❌ Défaut single query + oubli de AsSplitQuery() sur une requête à 2 collections
await MesurerReseau("AVANT : défaut single", async db =>
    (await db.Companies.AsNoTracking().Where(c => c.Id <= 2_000).Include(c => c.Starships).Include(c => c.Trades).ToListAsync()).Count);
```

### Exemple avec l'amélioration

```csharp
// ✔ Défaut du contexte en split (dans NewDb : .UseSqlServer(Cs, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)))
await MesurerReseau("APRÈS : défaut split", async db =>
    (await db.Companies.AsNoTracking().Where(c => c.Id <= 2_000).Include(c => c.Starships).Include(c => c.Trades).ToListAsync()).Count);

// ✔ Dérogation locale quand une seule petite collection suffit
await MesurerReseau("APRÈS : AsSingleQuery local", async db =>
    (await db.Companies.AsNoTracking().Where(c => c.Id <= 2_000).Include(c => c.Starships).AsSingleQuery().ToListAsync()).Count);
```
**À observer** : disparition de l'avertissement ; nombre de requêtes selon le réglage.

### Sans index / avec index et procédure stockée

Sans objet : c'est un réglage de comportement.

### 🧪 TP 3.8 — « Le bon défaut »

*Énoncé.* Votre application a 80 % de requêtes à 0-1 collection et 20 % à 2 collections ou plus. Quel défaut choisissez-vous ? Écrivez la configuration et deux exemples de dérogation.

<details><summary>Correction</summary>

Défaut **single query** (le plus économe en allers-retours pour 80 % des cas), avec `AsSplitQuery()` **explicite** sur les 20 % de requêtes à plusieurs collections ; ou défaut *split* et `AsSingleQuery()` pour les requêtes qui doivent être atomiques. Le critère est le **coût d'un aller-retour** (réseau) comparé au **coût d'une ligne répétée** (taille des lignes × produit des cardinalités). Dans tous les cas, laisser le défaut implicite avec l'avertissement ignoré est la mauvaise option.
</details>

---

## 3.9 Cohérence des requêtes fractionnées

**Axe concerné : justesse des données (et pas la vitesse).**

### La notion

Les requêtes d'un *split query* sont **indépendantes** : entre la lecture du parent et celle des enfants, une autre session peut **modifier ou supprimer** des lignes. Le résultat peut alors être incohérent (parent sans ses enfants, enfants orphelins). Une *single query* est, elle, **atomique** (un seul `SELECT`). Pour obtenir la cohérence avec le split : transaction **`Snapshot`** (ou `Serializable`) autour de la lecture.

### Dans quels cas on est concerné

- Données modifiées en concurrence pendant les lectures (système transactionnel actif).
- Génération de rapports ou d'exports qui doivent être cohérents.

### Mise en place (objets de démonstration)

```sql
CREATE TABLE dbo.Lab_Parent (Id int PRIMARY KEY, Nom nvarchar(30) NOT NULL);
CREATE TABLE dbo.Lab_Enfant (Id int PRIMARY KEY, ParentId int NOT NULL REFERENCES dbo.Lab_Parent(Id), Nom nvarchar(30) NOT NULL);
INSERT INTO dbo.Lab_Parent VALUES (1, N'Parent 1'), (2, N'Parent 2');
INSERT INTO dbo.Lab_Enfant VALUES (1,1,N'a'),(2,1,N'b'),(3,1,N'c'),(4,2,N'd');
```
```csharp
public class LabParent  { public int Id { get; set; } public string Nom { get; set; } = ""; public List<LabEnfant> Enfants { get; set; } = []; }
public class LabEnfant  { public int Id { get; set; } public int ParentId { get; set; } public string Nom { get; set; } = ""; }

public class LabCohCtx(DbContextOptions<LabCohCtx> o) : DbContext(o)
{
    public DbSet<LabParent> Parents => Set<LabParent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LabParent>(e => { e.ToTable("Lab_Parent"); e.HasMany(p => p.Enfants).WithOne().HasForeignKey(c => c.ParentId); });
        b.Entity<LabEnfant>(e => e.ToTable("Lab_Enfant"));
    }
}
```

### Exemple sans l'amélioration

Un intercepteur qui **supprime le parent n° 1 juste après la première requête** du split (via une **autre** connexion) simule une écriture concurrente :

```csharp
public sealed class SupprimeApresPremiereRequete(string cs) : DbCommandInterceptor
{
    private int _n;
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData e, DbDataReader result, CancellationToken ct = default)
    {
        if (++_n == 1)   // juste après la requête « parents »
        {
            await using var cn = new SqlConnection(cs); await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand("DELETE FROM dbo.Lab_Enfant WHERE ParentId = 1; DELETE FROM dbo.Lab_Parent WHERE Id = 1;", cn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return result;
    }
}
```
```csharp
// Exécution : réinitialisez d'abord les données avec le script de mise en place (DELETE puis INSERT).
var options = new DbContextOptionsBuilder<LabCohCtx>().UseSqlServer(Cs).AddInterceptors(new SupprimeApresPremiereRequete(Cs)).Options;
await using var c = new LabCohCtx(options);
var parents = await c.Parents.Include(p => p.Enfants).AsSplitQuery().ToListAsync();
foreach (var p in parents) Console.WriteLine($"{p.Nom} : {p.Enfants.Count} enfants");     // « Parent 1 : 0 enfant » alors qu'il en avait 3 au moment de la lecture
```
*(Attention : `ReaderExecutedAsync` n'est appelé qu'au retour du premier *reader* ; si votre version décale le moment, déplacez la suppression sur la deuxième exécution en conséquence.)*

### Exemple avec l'amélioration

```csharp
// ✔ Single query : un seul SELECT, atomique (la suppression n'a plus d'instant où s'insérer entre deux requêtes)
var p1 = await c.Parents.Include(p => p.Enfants).AsSingleQuery().ToListAsync();
```
```sql
-- ✔ Split + transaction SNAPSHOT (option de base à activer, à désactiver après le test)
ALTER DATABASE StarWarsTrade SET ALLOW_SNAPSHOT_ISOLATION ON;
```
```csharp
await using var tx = await c.Database.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot);
var p2 = await c.Parents.Include(p => p.Enfants).AsSplitQuery().ToListAsync();      // lectures cohérentes à l'instant de début
await tx.CommitAsync();
```
**À observer** : « enfants = 0 » en split sans protection ; résultat cohérent en *single* et en split sous `Snapshot` ; le coût du `Snapshot` (versions de lignes dans `tempdb`).

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 3.9 — « Détecter l'incohérence »

*Énoncé.* Écrivez un test qui échoue lorsqu'un split query lit un parent dont le nombre d'enfants a changé entre deux requêtes, et décrivez trois façons de l'éviter.

<details><summary>Correction</summary>

Test : lire d'abord le nombre d'enfants attendu avec un `COUNT` dans la **même** requête de parents (`Select(p => new { p, N = p.Enfants.Count })`), puis comparer à `p.Enfants.Count` après le split ; différent → incohérence. Trois remèdes : (1) *single query* ; (2) split dans une transaction `Snapshot` / `Serializable` ; (3) accepter l'incohérence si les données sont figées (dimensions rarement modifiées) et le documenter.
</details>

---

## 3.10 Le problème N+1

**Axe concerné : 🗄️ SQL (allers-retours) — le défaut de performance le plus fréquent avec un ORM.**

### La notion

On exécute **1** requête pour N parents, puis **N** requêtes (une par parent) pour leurs enfants : **N+1**. Causes : lazy loading (3.5), `Load()` en boucle (3.4), requête dans une boucle (`foreach (id in ids) await db.X.Where(...)`), accès à une navigation non incluse avec proxies. **Détection** : compter les requêtes (`CompteurSql`), journal, tests automatiques qui plafonnent le nombre de requêtes. **Corrections** : `Include`, projection, une requête avec `Contains(ids)` (par lots), split query, chargement groupé + *fixup* (3.13).

### Dans quels cas on est concerné

- Toute boucle contenant un `await` sur la base.
- Sérialisation d'un graphe d'objets, rendu de vues.

### Exemple sans l'amélioration

```csharp
// ❌ N+1 écrit « à la main »
await Mesurer("AVANT : N+1", async db =>
{
    var societes = await db.Companies.AsNoTracking().Where(c => c.Id <= 1_000).ToListAsync();
    var total = 0;
    foreach (var c in societes)
        total += await db.Trades.CountAsync(t => t.CompanyId == c.Id);              // 1 requête par société
    return total;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Une seule requête groupée
await Mesurer("APRÈS : GroupBy SQL", async db =>
    (await db.Trades.Where(t => t.CompanyId <= 1_000).GroupBy(t => t.CompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Sum(x => x.N));

// ✔ Ou une projection
await Mesurer("APRÈS : projection", async db =>
    (await db.Companies.Where(c => c.Id <= 1_000).Select(c => c.Trades.Count()).ToListAsync()).Sum());
```
**Détecter automatiquement** (à mettre dans vos tests d'intégration) :

```csharp
static async Task<T> AvecMaxRequetes<T>(CompteurSql c, int max, Func<Task<T>> action)
{
    c.Reset(); var r = await action();
    if (c.Requetes > max) throw new InvalidOperationException($"{c.Requetes} requêtes (max {max}) : N+1 probable");
    return r;
}
```
**À observer** : le compteur (1 001 → 1) ; dans Query Store / `sys.dm_exec_query_stats`, **une seule requête paramétrée avec un `execution_count` énorme** est la signature d'un N+1.

### Sans index / avec index

Chaque requête du N+1 filtre sur `CompanyId` : `IX_Trades_CompanyId` (FK) la rend rapide ; **sans** lui, 1 000 scans de 6 M de lignes. Le N+1 amplifie donc le moindre manque d'index.

### Comparaison avec une procédure stockée

Une procédure qui fait la boucle côté serveur (curseur) évite les allers-retours mais reste un traitement ligne à ligne ; la bonne forme est **ensembliste** (`GROUP BY`), identique à la version LINQ.

### 🧪 TP 3.10 — « Plafonner les requêtes »

*Énoncé.* Écrivez une méthode `VentesParSociete(db, int maxId)` qui retourne, pour chaque société d'`Id ≤ maxId`, le total `Quantity × UnitPrice`. Écrivez-la en N+1, puis corrigez-la, puis protégez-la avec `AvecMaxRequetes(compteur, 2, …)`.

<details><summary>Correction</summary>

```csharp
static async Task<Dictionary<int, decimal>> VentesParSociete(StarWarsContext db, int maxId) =>
    (await db.Trades.Where(t => t.CompanyId <= maxId)
        .GroupBy(t => t.CompanyId).Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice) }).ToListAsync())
        .ToDictionary(x => x.Key, x => x.V);

await AvecMaxRequetes(compteur, 2, () => VentesParSociete(db, 1_000));
```
La garde échoue (lève une exception) dès que quelqu'un réintroduit une boucle : c'est le **filet anti-régression** du N+1.
</details>

---

## 3.11 `AutoInclude` / `IgnoreAutoIncludes`

**Axe concerné : 🗄️ SQL (jointures implicites) + 🧠 mémoire.**

### La notion

`AutoInclude` demande à EF de charger une navigation **dans toutes les requêtes** d'une entité, sans `Include` explicite. Pratique pour une navigation **toujours nécessaire** (référence 1-1 ou N-1 légère), dangereux pour une **collection** : chaque lecture de l'entité paie la jointure et le volume, même quand on n'en a pas besoin. `IgnoreAutoIncludes()` désactive l'inclusion pour une requête.

### Dans quels cas on est concerné

- Navigation de référence (`Planet.StarSystem`) utile dans 95 % des cas.
- Eviter l'oubli d'un `Include` sur un objet de valeur lié.

### Exemple sans l'amélioration

Dans votre classe partielle du contexte :

```csharp
partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Planet>().Navigation(p => p.Markets).AutoInclude();        // une COLLECTION en AutoInclude
}
```
```csharp
// ❌ Chaque lecture de planète charge ses marchés, même pour un simple décompte
await MesurerReseau("AVANT : AutoInclude collection", async db =>
    (await db.Planets.AsNoTracking().Where(p => p.Id <= 20_000).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Désactivé pour cette requête
await MesurerReseau("APRÈS : IgnoreAutoIncludes", async db =>
    (await db.Planets.AsNoTracking().IgnoreAutoIncludes().Where(p => p.Id <= 20_000).ToListAsync()).Count, afficherSql: true);
```
Mieux : **retirer** l'`AutoInclude` de la collection et le réserver aux références légères (`Navigation(p => p.StarSystem).AutoInclude()`).

**À observer** : le `LEFT JOIN` ajouté au SQL de toutes les requêtes de `Planet` ; lignes et Mo reçus ; l'effet sur les requêtes **qui n'utilisent pas** la navigation (par exemple une projection n'en tient pas compte).

### Sans index / avec index et procédure stockée

Sans objet (même remarque que 3.1).

### 🧪 TP 3.11 — « Qui paie l'AutoInclude ? »

*Énoncé.* Avec `AutoInclude` sur `Planet.Markets`, comparez le SQL de : (a) `db.Planets.ToList()`, (b) `db.Planets.Select(p => p.Name)`, (c) `db.Planets.Include(p => p.Inhabitants)`, (d) `db.Planets.IgnoreAutoIncludes()`. Lesquelles paient la jointure ?

<details><summary>Correction</summary>

(a) et (c) paient la jointure `Markets` ; (b) non (la projection ne matérialise pas d'entité) ; (d) non. C'est la preuve que l'`AutoInclude` s'applique à **l'entité matérialisée**, pas à la table : vérifiez avec `ToQueryString()` pour chaque cas.
</details>

---

## 3.12 Filtres globaux

**Axe concerné : 🗄️ SQL (prédicat ajouté partout) + justesse.**

### La notion

`HasQueryFilter` ajoute un prédicat **à toutes** les requêtes d'une entité (suppression logique, multi-locataire). Il s'applique **aussi** aux entités incluses et aux navigations. `IgnoreQueryFilters()` le désactive pour une requête ; EF 10 introduit des **filtres nommés** (`HasQueryFilter("nom", …)` et `IgnoreQueryFilters(["nom"])`) *(à vérifier selon votre version)*. Coûts : un prédicat **sur une colonne non indexée** ou **impliquant une jointure** s'ajoute à chaque requête.

### Dans quels cas on est concerné

- Suppression logique (`IsDeleted`), isolation par locataire.
- Filtre dépendant d'une **navigation** (`Planet.StarSystem.SectorId == tenant`) : jointure systématique.

### Exemple sans l'amélioration

Dans la classe partielle (variante à tester dans un **contexte dédié** pour ne pas altérer les autres essais) :

```csharp
modelBuilder.Entity<Planet>().HasQueryFilter(p => p.StarSystem.SectorId <= 500_000);      // filtre via navigation = jointure partout
```
```csharp
// ❌ Chaque requête sur Planets joint StarSystems, même un simple Count ou une lecture par clé
await Mesurer("AVANT : filtre par navigation", db => db.Planets.CountAsync(p => p.Population > 99_000_000_000), afficherSql: true);
await Mesurer("AVANT : lecture par clé", async db => (await db.Planets.FirstAsync(p => p.Id == 1_234_567)).Name, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Filtre sur une colonne de la table elle-même (dénormaliser SectorId dans Planets), ou…
modelBuilder.Entity<Market>().HasQueryFilter(m => !m.IsBlackMarket);                     // colonne locale : prédicat simple
// ✔ …désactivation ciblée quand le filtre n'est pas voulu
await Mesurer("APRÈS : IgnoreQueryFilters", db => db.Markets.IgnoreQueryFilters().CountAsync(), afficherSql: true);
```
**À observer** : le `WHERE`/`INNER JOIN` ajouté **à tout** (comparez `ToQueryString()` avec et sans `IgnoreQueryFilters()`) ; le plan ; l'interaction avec `Include` : le filtre s'applique aux enfants chargés.

### Sans index / avec index

Un filtre sur une colonne **peu sélective** et non indexée n'influence presque pas les lectures par clé mais pèse sur tous les scans. Un **index filtré** reflète parfois le filtre global :

```sql
CREATE INDEX IX_Lab_Markets_NonNoirs ON dbo.Markets (PlanetId) INCLUDE (Name, Kind, TaxRate, OpenedOn) WHERE IsBlackMarket = 0;
DROP INDEX IX_Lab_Markets_NonNoirs ON dbo.Markets;
```
(L'optimiseur n'utilise l'index filtré que si le prédicat est connu à la compilation : avec un filtre `!IsBlackMarket` constant, c'est le cas.)

### Comparaison avec une procédure stockée

Une procédure ou une **vue** peut porter le filtre dans la base (`CREATE VIEW dbo.Lab_vwMarchesPublics AS SELECT … WHERE IsBlackMarket = 0`) et être mappée (`ToView`). Avantage : filtre appliqué à **tous** les clients de la base ; inconvénient : le filtre ne se désactive pas par requête.

### 🧪 TP 3.12 — « Combien coûte le filtre ? »

*Énoncé.* Mesurez `Markets.CountAsync()` et la lecture d'un marché par `Id` avec et sans le filtre global `!IsBlackMarket`, avec et sans l'index filtré ci-dessus. Concluez sur le coût d'un filtre global **sur colonne locale**.

<details><summary>Correction</summary>

Lecture par clé : coût **négligeable** (le prédicat s'ajoute à un seek). `Count` : ajoute un prédicat sur tout un scan ; l'index filtré le réduit. Le coût important d'un filtre global vient des cas où il fait intervenir une **jointure** (filtre par navigation), pas d'un prédicat de colonne.
</details>

---

## 3.13 *Fixup*

**Axe concerné : 🗄️ SQL (évite la jointure) + ⚙️ .NET (tracking nécessaire).**

### La notion

Le *change tracker* **relie automatiquement** les entités qu'il suit : si vous chargez des `Planet` puis des `Market`, `planete.Markets` est **déjà alimentée** (et `marche.Planet`), sans `Include`. On peut donc fractionner **à la main** un chargement : deux requêtes simples, sans jointure ni produit. Cela ne fonctionne qu'avec le **suivi** (`AsNoTracking` désactive le fixup ; `AsNoTrackingWithIdentityResolution` le rétablit pour une même requête).

### Dans quels cas on est concerné

- Alternative légère à `AsSplitQuery` avec **filtres différents** pour parents et enfants.
- Eviter la répétition des colonnes du parent.

### Exemple sans l'amélioration

```csharp
// ❌ Include en single query, deux collections : produit cartésien
await MesurerReseau("AVANT : Include ×2", async db =>
    (await db.Companies.Where(c => c.Id <= 2_000).Include(c => c.Starships).Include(c => c.Trades).AsSingleQuery().ToListAsync()).Count);
```

### Exemple avec l'amélioration

```csharp
// ✔ Deux requêtes simples + fixup
await MesurerReseau("APRÈS : chargement manuel + fixup", async db =>
{
    var societes = await db.Companies.Where(c => c.Id <= 2_000).ToListAsync();
    await db.Starships.Where(s => s.CompanyId <= 2_000).LoadAsync();         // alimente company.Starships
    await db.Trades.Where(t => t.CompanyId <= 2_000).LoadAsync();            // alimente company.Trades
    return societes.Sum(c => c.Starships.Count + c.Trades.Count);
});
```
**À observer** : 3 requêtes sans jointure ; lignes lues = somme (pas produit) ; `societes[0].Starships.Count` rempli sans `Include`.

### Sans index / avec index

Chaque `Load` filtre par clé étrangère (`CompanyId <= 2000`) : les index de FK (`IX_Starships_CompanyId`, `IX_Trades_CompanyId`) servent à lire la plage.

### Comparaison avec une procédure stockée

Équivalent d'une procédure à plusieurs jeux de résultats ; ici, EF fait le rapprochement des objets via le suivi.

### 🧪 TP 3.13 — « Charger des enfants différemment »

*Énoncé.* Chargez les sociétés d'`Id ≤ 2 000` avec **seulement les vaisseaux opérationnels** (`IsOperational`) et **seulement les échanges de l'année 2025**, par deux requêtes + fixup. Pourquoi un `Include` classique ne le permet-il pas simplement (indice : filtres différents) ? Comparez avec `Include` filtré.

<details><summary>Correction</summary>

```csharp
var s = await db.Companies.Where(c => c.Id <= 2_000).ToListAsync();
await db.Starships.Where(x => x.CompanyId <= 2_000 && x.IsOperational).LoadAsync();
await db.Trades.Where(t => t.CompanyId <= 2_000 && t.TradedAt >= new DateTime(2025,1,1)).LoadAsync();
```
Un `Include` filtré existe (3.6) et fait la même chose en une requête pour chaque collection ; le chargement manuel est utile quand le filtre dépend d'un **calcul préalable** (liste d'ids, résultat d'une autre requête) ou quand on veut contrôler chaque requête. Attention : `company.Starships` ne contient alors que les vaisseaux **chargés** — ce n'est plus la collection complète.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Markets_Planet_Name    ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Markets_NonNoirs       ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Trades_Market_Date     ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Good_Price      ON dbo.Trades;

DROP PROCEDURE IF EXISTS dbo.Lab_PlanetesEtMarches;
DROP PROCEDURE IF EXISTS dbo.Lab_DerniersEchangesParMarche;

DROP VIEW IF EXISTS dbo.Lab_vwMarchesPublics;      -- si vous avez créé la vue de 3.12
DROP TABLE IF EXISTS dbo.Lab_Enfant;
DROP TABLE IF EXISTS dbo.Lab_Parent;

-- Si vous avez activé l'isolation snapshot pour le TP 3.9 :
ALTER DATABASE StarWarsTrade SET ALLOW_SNAPSHOT_ISOLATION OFF;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
SELECT name, snapshot_isolation_state_desc FROM sys.databases WHERE name = 'StarWarsTrade';   -- doit être OFF
```
