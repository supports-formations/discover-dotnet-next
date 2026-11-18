# 06 — Performance : techniques simples

> Suite de [`05-ecritures.md`](05-ecritures.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans, `ToQueryString`) et `MesurerReseau` du [03](03-donnees-liees.md).
> Ce chapitre est la **liste de contrôle** que l'on applique à toute requête LINQ avant de chercher plus compliqué. Chaque technique a déjà été **expliquée** dans un chapitre précédent ; ici on la **rejoue sur d'autres tables et d'autres scénarios**, avec ce qui change quand on y ajoute un index ou une procédure stockée. Les renvois indiquent où lire le détail.
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> Index de démonstration : `IX_Lab_` ; objets SQL : `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

| # | Technique | Rappel |
|---|---|---|
| [6.1](#61-projeter) | Projeter plutôt que charger l'entité | 1.1, 4.6 |
| [6.2](#62-asnotracking) | `AsNoTracking` pour toute lecture seule | 4.1 |
| [6.3](#63-filtrer-trier-limiter-côté-serveur) | Filtrer, trier, limiter **côté serveur** | 0.5, 0.6 |
| [6.4](#64-any-plutôt-que-count--0) | `Any()` plutôt que `Count() > 0` | 1.3 |
| [6.5](#65-pagination-bornée) | Pagination bornée (`Take`, `OrderBy` stable) | 1.2 |
| [6.6](#66-contains-plutôt-que-des-requêtes-en-boucle) | `Contains` plutôt que des requêtes en boucle | 1.8, 3.10 |
| [6.7](#67-des-index-alignés-sur-les-prédicats-et-les-tris) | Des index alignés sur les prédicats et les tris | 1.2, 2.9 |
| [6.8](#68-sargabilité) | Sargabilité : pas de fonction sur la colonne | 2.9 |
| [6.9](#69-concat-plutôt-que-union) | `Concat` plutôt que `Union` | 1.7 |
| [6.10](#610-lire-le-sql-généré) | Lire le SQL généré | 2.1 |
| [6.11](#611-taguer-les-requêtes) | `TagWith` pour retrouver la requête | 2.1 |
| [6.12](#612-agréger-côté-serveur) | Agréger côté serveur | 1.4, 1.5 |
| [6.13](#613-ne-pas-include-ce-quun-select-suffit-à-fournir) | Ne pas `Include` ce qu'un `Select` suffit à fournir | 3.1, 3.3 |
| [6.14](#614-le-coût-des-filtres-globaux) | Penser au coût des filtres globaux | 3.12 |

Tables utilisées ici (pour varier) : `Starships` (2 M), `Species` (1 M), `Markets` (2 M), `Companies` (1 M), `FactionPlanets` (2 M), `Sectors` (1 M), `Inhabitants` (4 M). Colonnes : `Starships(Id, CompanyId, Name, Model, CargoCapacity, HyperdriveRating, BuiltOn, IsOperational)`, `Species(Id, HomeworldId, Name, Classification, Language, AverageLifespan, AverageHeightCm, IsSentient)`, `FactionPlanets(FactionId, PlanetId, Influence, ControlSince, IsCapital)`.

---

## 6.1 Projeter

**Axe concerné : 🗄️ SQL (colonnes, index couvrant) + 🧠 mémoire.**

### La technique

Ne ramener que les colonnes utilisées (`Select` vers type anonyme/DTO) : moins de données transférées, aucun suivi, **index couvrant** possible.

### Dans quels cas on est concerné

Listes, API de consultation, exports : on affiche 2-3 champs d'une entité qui en a 8 ou plus.

### Exemple sans l'amélioration

```csharp
// ❌ Entités complètes (8 colonnes) pour afficher nom et modèle
await Mesurer("AVANT : entité", async db =>
    (await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Deux colonnes
await Mesurer("APRÈS : projection", async db =>
    (await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).Select(s => new { s.Name, s.Model }).ToListAsync()).Count, afficherSql: true);
```
**À observer** : colonnes du `SELECT` (8 → 2) ; allocations ; Mo reçus (`MesurerReseau`).

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;     -- Index Seek (FK) + Key Lookup par ligne
GO
CREATE INDEX IX_Lab_Starships_Cover ON dbo.Starships (CompanyId) INCLUDE (IsOperational, Name, Model);
GO
SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= 20000 AND IsOperational = 1;     -- Index Seek seul, plus de lookup
DROP INDEX IX_Lab_Starships_Cover ON dbo.Starships;
```
L'**entité complète** ne profite pas de cet index (il lui faudrait les 8 colonnes).

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_VaisseauxActifs @CompanyMax int
AS BEGIN SET NOCOUNT ON; SELECT Name, Model FROM dbo.Starships WHERE CompanyId <= @CompanyMax AND IsOperational = 1; END
```
```csharp
public record Vaisseau(string Name, string Model);
await Mesurer("PROC", async db => (await db.Database.SqlQuery<Vaisseau>($"EXEC dbo.Lab_VaisseauxActifs {20_000}").ToListAsync()).Count);
```
Même plan que la projection LINQ : la procédure n'apporte rien de plus.

### 🧪 TP 6.1 — « Réduire sans casser »

*Énoncé.* Un écran affiche, pour chaque vaisseau opérationnel des 20 000 premières sociétés, `"Nom (Modèle)"`. Écrivez la version entité puis la version projection ; vérifiez que le résultat est identique et comparez requêtes, Mo reçus, temps, avec puis sans l'index couvrant.

<details><summary>Correction</summary>

```csharp
var avant = (await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).ToListAsync()).Select(s => $"{s.Name} ({s.Model})").OrderBy(x => x).ToList();
var apres = (await db.Starships.Where(s => s.CompanyId <= 20_000 && s.IsOperational).Select(s => new { s.Name, s.Model }).ToListAsync()).Select(s => $"{s.Name} ({s.Model})").OrderBy(x => x).ToList();
Console.WriteLine(avant.SequenceEqual(apres));   // true
```
Gain principal : colonnes et suivi ; avec l'index couvrant, le plan passe de *Seek + Key Lookup* à *Seek seul*.
</details>

---

## 6.2 `AsNoTracking`

**Axe concerné : ⚙️ .NET + 🧠 mémoire (aucun effet sur le SQL).**

### La technique

Toute lecture dont les entités ne seront **pas modifiées** : `AsNoTracking()` (ou défaut global, voir 4.4).

### Dans quels cas on est concerné

Rapports, exports, GET d'API, caches en lecture.

### Exemple sans l'amélioration

```csharp
// ❌ 500 000 vaisseaux suivis pour une simple moyenne
await Mesurer("AVANT : avec suivi", async db =>
{
    var l = await db.Starships.Where(s => s.Id <= 500_000).ToListAsync();
    return $"moyenne = {l.Average(s => s.HyperdriveRating):N3} ; suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : AsNoTracking", async db =>
{
    var l = await db.Starships.AsNoTracking().Where(s => s.Id <= 500_000).ToListAsync();
    return $"moyenne = {l.Average(s => s.HyperdriveRating):N3} ; suivies : {db.ChangeTracker.Entries().Count():N0}";
});
// ✔✔ Et mieux encore : la moyenne dans SQL
await Mesurer("APRÈS : AverageAsync", async db => $"moyenne = {await db.Starships.Where(s => s.Id <= 500_000).AverageAsync(s => s.HyperdriveRating):N3}");
```
**À observer** : entités suivies, Mo alloués, GC ; le même SQL pour les deux premières variantes.

### Sans index / avec index et procédure stockée

Sans objet : le suivi n'agit pas sur le SQL.

### 🧪 TP 6.2 — « Trouver les lectures qui suivent »

*Énoncé.* Écrivez une méthode utilitaire `NbSuivies(db)` et appliquez-la après trois lectures : entité, projection, entité avec `AsNoTracking`. Complétez : quelle est la seule lecture suivie ?

<details><summary>Correction</summary>

```csharp
static int NbSuivies(StarWarsContext db) => db.ChangeTracker.Entries().Count();
```
Seule la lecture d'**entités sans `AsNoTracking`** est suivie ; la projection vers un type anonyme ne l'est pas (sauf si elle contient une entité, voir 4.6).
</details>

---

## 6.3 Filtrer, trier, limiter côté serveur

**Axe concerné : 🗄️ SQL (lignes transférées, tri) + 🧠 mémoire.**

### La technique

Ne **jamais** appeler `ToList()`/`AsEnumerable()` **avant** `Where`, `OrderBy`, `Take`, `Count`. Les opérateurs placés après se feraient en mémoire (voir 0.5, 0.6).

### Dans quels cas on est concerné

Code qui matérialise « pour être sûr » avant de filtrer ou de trier.

### Exemple sans l'amélioration

```csharp
// ❌ 1 M d'espèces rapatriées pour en afficher 10
await Mesurer("AVANT : ToList puis tri et Take", async db =>
{
    var tout = await db.Species.AsNoTracking().ToListAsync();
    return tout.OrderByDescending(s => s.AverageLifespan).ThenBy(s => s.Id).Take(10).Count();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Top 10 côté SQL
await Mesurer("APRÈS : OrderBy + Take SQL", async db =>
    (await db.Species.AsNoTracking().OrderByDescending(s => s.AverageLifespan).ThenBy(s => s.Id).Take(10).ToListAsync()).Count, afficherSql: true);
```
**À observer** : `SELECT TOP(@p) … ORDER BY [AverageLifespan] DESC, [Id]` ; lignes lues (1 M → 10) ; dans le plan, un **`Top N Sort`** (lecture de toute la table pour garder 10 lignes).

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
SELECT TOP (10) * FROM dbo.Species ORDER BY AverageLifespan DESC, Id;                   -- scan complet + Top N Sort
GO
CREATE INDEX IX_Lab_Species_Lifespan ON dbo.Species (AverageLifespan DESC, Id);
GO
SELECT TOP (10) * FROM dbo.Species ORDER BY AverageLifespan DESC, Id;                   -- lecture de 10 lignes de l'index + 10 lookups
DROP INDEX IX_Lab_Species_Lifespan ON dbo.Species;
```
**À observer** : *logical reads* de plusieurs milliers à quelques dizaines ; disparition du `Sort`.

### Comparaison avec une procédure stockée

Aucune différence de plan avec `SELECT TOP (@N) …` ; l'intérêt éventuel d'une procédure est de **figer** la requête (droits), pas de la rendre plus rapide.

### 🧪 TP 6.3 — « Les 10 plus grandes espèces sentientes »

*Énoncé.* Retournez les 10 espèces sentientes de plus grande taille moyenne (`AverageHeightCm`), sans matérialiser la table. Mesurez avec/sans index `(IsSentient, AverageHeightCm DESC)`. Quelle est la forme de l'index qui évite le tri ?

<details><summary>Correction</summary>

```csharp
db.Species.AsNoTracking().Where(s => s.IsSentient).OrderByDescending(s => s.AverageHeightCm).ThenBy(s => s.Id).Take(10)
```
Index : `CREATE INDEX IX_Lab_Species_Sentient_Height ON dbo.Species (IsSentient, AverageHeightCm DESC, Id);` — la colonne d'**égalité** (`IsSentient`) en premier, puis la colonne de **tri**. L'ordre inverse ne permettrait pas d'éviter le tri.
</details>

---

## 6.4 `Any()` plutôt que `Count() > 0`

**Axe concerné : 🗄️ SQL (arrêt anticipé).**

### La technique

Pour tester l'existence : `Any(pred)` (→ `EXISTS`) ; jamais `Count(pred) > 0`.

### Dans quels cas on est concerné

Validation (« ce SKU existe-t-il ? »), garde-fous avant écriture, règles métier.

### Exemple sans l'amélioration

Calibrez d'abord un seuil qui donne de **nombreux** résultats (par exemple `CargoCapacity > X` avec des millions de vaisseaux concernés) :

```csharp
Console.WriteLine(await db.Starships.MinAsync(s => s.CargoCapacity) + " .. " + await db.Starships.MaxAsync(s => s.CargoCapacity));
```
```csharp
// ❌ Compte tous les vaisseaux concernés pour savoir s'il y en a au moins un
await Mesurer("AVANT : Count() > 0", async db => await db.Starships.CountAsync(s => s.CargoCapacity > 100) > 0, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : Any", db => db.Starships.AnyAsync(s => s.CargoCapacity > 100), afficherSql: true);
// Cas défavorable de Any : aucun résultat (prenez un seuil au-delà du maximum)
await Mesurer("Any sans résultat", db => db.Starships.AnyAsync(s => s.CargoCapacity > 999_999_999), afficherSql: true);
```
**À observer** : `EXISTS` ; *logical reads* de `Any` (très peu quand le premier résultat est proche) contre `COUNT(*)` (tout) ; le **cas sans résultat** où les deux parcourent tout.

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Starships_Cargo ON dbo.Starships (CargoCapacity);
-- relancer « Any sans résultat » : Seek immédiat, quelques lectures
DROP INDEX IX_Lab_Starships_Cargo ON dbo.Starships;
```

### Comparaison avec une procédure stockée

Sans objet (`IF EXISTS (…)` donne le même plan).

### 🧪 TP 6.4 — « Valider l'unicité »

*Énoncé.* Écrivez `SkuExiste(db, sku)` de deux manières (`Count() > 0`, `Any`) et mesurez sur un SKU existant puis inexistant. Que remarquez-vous (indice : `Sku` est indexé et unique) ?

<details><summary>Correction</summary>

Sur une colonne **unique et indexée**, `Count` et `Any` coûtent presque la même chose (un *seek* retourne au plus une ligne). La règle « `Any` plutôt que `Count > 0` » protège surtout les colonnes **non indexées ou non uniques**, où l'écart est considérable.
</details>

---

## 6.5 Pagination bornée

**Axe concerné : 🗄️ SQL + 🧠 mémoire + justesse.**

### La technique

Toujours un `Take` (borne), toujours un `OrderBy` **déterministe** (clé unique en dernier critère). Pour de grandes profondeurs : pagination par clé (voir 1.2).

### Dans quels cas on est concerné

Endpoints de liste, tableaux, exports interactifs.

### Exemple sans l'amélioration

```csharp
// ❌ Aucune borne : l'endpoint retourne la table entière
await Mesurer("AVANT : pas de Take", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).OrderBy(m => m.OpenedOn).ToListAsync()).Count);

// ❌ Take sans OrderBy : résultat non déterministe (EF journalise un avertissement)
await Mesurer("AVANT : Take sans OrderBy", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).Take(50).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : page de 50, tri déterministe", async db =>
    (await db.Markets.AsNoTracking().Where(m => m.Kind == 2).OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).Skip(1_000).Take(50).ToListAsync()).Count, afficherSql: true);
```
**À observer** : `OFFSET … FETCH` ; lignes lues ; avertissement `RowLimitingOperationWithoutOrderByWarning` dans le journal pour `Take` sans tri.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
SELECT * FROM dbo.Markets WHERE Kind = 2 ORDER BY OpenedOn, Id OFFSET 1000 ROWS FETCH NEXT 50 ROWS ONLY;      -- scan + tri de tous les marchés Kind=2
GO
CREATE INDEX IX_Lab_Markets_Kind_Date ON dbo.Markets (Kind, OpenedOn, Id);
GO
SELECT * FROM dbo.Markets WHERE Kind = 2 ORDER BY OpenedOn, Id OFFSET 1000 ROWS FETCH NEXT 50 ROWS ONLY;      -- Seek, lit 1 050 lignes, aucun tri
DROP INDEX IX_Lab_Markets_Kind_Date ON dbo.Markets;
```

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_PageMarches @Kind int, @Decalage int, @Taille int
AS BEGIN SET NOCOUNT ON;
  SELECT Id, PlanetId, Name, Kind, TaxRate, IsBlackMarket, OpenedOn FROM dbo.Markets WHERE Kind = @Kind
  ORDER BY OpenedOn, Id OFFSET @Decalage ROWS FETCH NEXT @Taille ROWS ONLY; END
```
Même plan que le LINQ ; l'avantage éventuel est de **plafonner** `@Taille` à l'intérieur de la procédure.

### 🧪 TP 6.5 — « Le garde-fou de taille »

*Énoncé.* Écrivez une méthode `Page(db, int page, int taille)` qui **plafonne** `taille` à 100 et fixe un tri stable. Montrez le SQL pour `taille = 100000`.

<details><summary>Correction</summary>

```csharp
static Task<List<Market>> Page(StarWarsContext db, int page, int taille)
{
    taille = Math.Clamp(taille, 1, 100);
    return db.Markets.AsNoTracking().OrderBy(m => m.OpenedOn).ThenBy(m => m.Id).Skip(page * taille).Take(taille).ToListAsync();
}
```
`Take(@p)` envoie 100 quel que soit l'appelant : une API publique qui laisse l'appelant choisir la taille est un déni de service en puissance.
</details>

---

## 6.6 `Contains` plutôt que des requêtes en boucle

**Axe concerné : 🗄️ SQL (allers-retours).**

### La technique

Pour chercher N éléments par clé : **une requête `Contains`** (ou quelques lots, voir 1.8) plutôt que N requêtes (N+1, voir 3.10).

### Dans quels cas on est concerné

Recherches par lot d'identifiants, enrichissement d'une liste.

### Exemple sans l'amélioration

```csharp
var codes = await NewDb().Sectors.AsNoTracking().OrderBy(s => s.Id).Take(1_000).Select(s => s.Code).ToListAsync();

// ❌ 1 000 requêtes
await Mesurer("AVANT : 1 par 1", async db =>
{
    var n = 0; foreach (var c in codes) n += await db.Sectors.CountAsync(s => s.Code == c);
    return n;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Une requête (IN)
await Mesurer("APRÈS : Contains", db => db.Sectors.CountAsync(s => codes.Contains(s.Code)), afficherSql: true);
```
**À observer** : 1 000 requêtes → 1 ; la forme du `IN` (voir 2.6) ; **le type du paramètre** : `Code` est `varchar`, regardez dans le plan s'il y a une conversion implicite (2.10).

### Sans index / avec index

`Sectors.Code` possède un **index unique** : chaque valeur est un *seek*. Pour une colonne non indexée, `Contains` scanne la table **une** fois (contre N scans en boucle).

### Comparaison avec une procédure stockée

Pour de très longues listes : paramètre table (TVP) — voir le chapitre 10.

### 🧪 TP 6.6 — « Enrichir une liste »

*Énoncé.* On vous fournit 5 000 identifiants de vaisseaux ; retournez leur nom et le nom de leur société. Écrivez la version en boucle puis la version en lots de 1 000 avec projection. Comparez le nombre de requêtes et le temps.

<details><summary>Correction</summary>

```csharp
var ids = Enumerable.Range(1, 5_000).ToList();
var res = new List<(string Vaisseau, string Societe)>();
foreach (var lot in ids.Chunk(1_000))
    res.AddRange((await db.Starships.AsNoTracking().Where(s => lot.Contains(s.Id)).Select(s => new { s.Name, Societe = s.Company.Name }).ToListAsync())
                 .Select(x => (x.Name, x.Societe)));
```
5 requêtes au lieu de 5 000 (ou 10 000 avec une requête de société par vaisseau).
</details>

---

## 6.7 Des index alignés sur les prédicats et les tris

**Axe concerné : 🗄️ SQL (seek, tri évité, lookups évités).**

### La technique

Un index utile suit la règle : **colonnes d'égalité d'abord**, puis la colonne de **tri/plage**, puis les colonnes lues en `INCLUDE`. Il doit correspondre à ce que la requête **filtre et ordonne** réellement.

### Dans quels cas on est concerné

Requêtes fréquentes avec filtre + tri + `Take` ; requêtes lentes dont le plan montre un `Sort` ou un `Scan`.

### Exemple sans l'amélioration

```csharp
// ❌ Les 20 habitants actifs les plus riches d'un genre : 4 M de lignes à trier
await Mesurer("AVANT : sans index adapté", async db =>
    (await db.Inhabitants.AsNoTracking().Where(i => i.Gender == 1 && i.IsActive)
        .OrderByDescending(i => i.Credits).ThenBy(i => i.Id).Take(20).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```sql
SET STATISTICS IO, TIME ON;
-- Plan actuel : Clustered Index Scan + Top N Sort
CREATE INDEX IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants (Gender, IsActive, Credits DESC, Id);
-- Même requête C# : Seek sur (Gender=1, IsActive=1) puis lecture dans l'ordre de Credits, 20 lignes, + 20 Key Lookups
```
```csharp
// relancer exactement le même code C#
```
**Variantes à essayer** :
- Index dans le **mauvais ordre** `(Credits DESC, Gender, IsActive, Id)` : comparez le plan.
- Index `(Gender, IsActive) INCLUDE (Credits, …)` sans `Credits` en clé : le tri revient.
- Requête ordonnée par une **autre** colonne (`BirthYear`) : l'index ne sert plus à éviter le tri.

### Sans index / avec index

C'est l'objet de la section : voir ci-dessus. Complément : `SET STATISTICS IO` avant/après ; **index manquants suggérés** :

```sql
SELECT TOP 10 mid.statement, mid.equality_columns, mid.inequality_columns, mid.included_columns, migs.user_seeks, migs.avg_user_impact
FROM sys.dm_db_missing_index_details mid
JOIN sys.dm_db_missing_index_groups mig ON mig.index_handle = mid.index_handle
JOIN sys.dm_db_missing_index_group_stats migs ON migs.group_handle = mig.index_group_handle
ORDER BY migs.avg_user_impact * migs.user_seeks DESC;
```
(à lire avec **esprit critique** : ce sont des suggestions, pas des ordonnances.)

### Comparaison avec une procédure stockée

Sans objet : l'index s'applique à toute requête, quelle que soit son origine.

### 🧪 TP 6.7 — « Dessiner l'index »

*Énoncé.* Pour `db.Inhabitants.Where(i => i.PlanetId == 12 && i.IsActive).OrderBy(i => i.LastName).Take(10)`, proposez l'index idéal, créez-le et prouvez avec le plan. Que se passe-t-il si l'on remplace `== 12` par `< 50000` ?

<details><summary>Correction</summary>

Index : `(PlanetId, IsActive, LastName)` (égalités puis tri). Avec `PlanetId < 50000` (une **plage**), les lignes lues dans l'ordre de l'index ne sont plus triées sur `LastName` globalement : le tri **réapparaît**. Règle : tout ce qui suit une colonne en **plage** dans la clé ne peut plus servir au tri.
</details>

---

## 6.8 Sargabilité

**Axe concerné : 🗄️ SQL (seek vs scan).**

### La technique

Pas de fonction ni de calcul sur la **colonne** : déplacer le calcul du côté de la **constante** (voir 2.9).

### Dans quels cas on est concerné

Prédicats sur `Year`, `Month`, `ToLower`, `Substring`, arithmétique.

### Exemple sans l'amélioration

```csharp
// ❌ Fonction sur la colonne de date (Companies.FoundedOn)
await Mesurer("AVANT : Year ==", db => db.Companies.CountAsync(c => c.FoundedOn.Year == 1999), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
var debut = new DateOnly(1999, 1, 1); var fin = new DateOnly(2000, 1, 1);
await Mesurer("APRÈS : intervalle", db => db.Companies.CountAsync(c => c.FoundedOn >= debut && c.FoundedOn < fin), afficherSql: true);
```
**À observer** : `DATEPART(year, [FoundedOn]) = 1999` contre `>= @debut AND < @fin`.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
CREATE INDEX IX_Lab_Companies_FoundedOn ON dbo.Companies (FoundedOn);
SELECT COUNT(*) FROM dbo.Companies WHERE YEAR(FoundedOn) = 1999;                              -- Index Scan
SELECT COUNT(*) FROM dbo.Companies WHERE FoundedOn >= '1999-01-01' AND FoundedOn < '2000-01-01';   -- Index Seek
DROP INDEX IX_Lab_Companies_FoundedOn ON dbo.Companies;
```
Sans l'index, les deux écritures scannent : **il faut les deux** (prédicat sargable *et* index).

### Comparaison avec une procédure stockée

Les mêmes règles s'appliquent : un `WHERE YEAR(col) = @a` dans une procédure n'est pas sargable.

### 🧪 TP 6.8 — « Réécrire »

*Énoncé.* Réécrivez de façon sargable : `c.Name.Substring(0, 3) == "Kam"`, `s.CargoCapacity / 10 > 500`, `m.OpenedOn.AddYears(1) > DateOnly.FromDateTime(DateTime.Today)`. Donnez le SQL de chaque version.

<details><summary>Correction</summary>

`c.Name.StartsWith("Kam")` ; `s.CargoCapacity > 5_000` (attention à la division entière : `/ 10 > 500` ⇔ `>= 5 010` pour des entiers — vérifier l'équivalence avec le SQL) ; `m.OpenedOn > DateOnly.FromDateTime(DateTime.Today).AddYears(-1)`. À chaque fois, `ToQueryString()` avant/après : le calcul se déplace côté paramètre.
</details>

---

## 6.9 `Concat` plutôt que `Union`

**Axe concerné : 🗄️ SQL (dédoublonnage évité).**

### La technique

`Union` dédoublonne (tri ou hachage de tout) ; `Concat` (`UNION ALL`) ne coûte que la lecture. Utiliser `Union` **seulement** si les doublons sont possibles *et* indésirables (voir 1.7).

### Dans quels cas on est concerné

Fusion de sources disjointes (statuts opposés, périodes distinctes).

### Exemple sans l'amélioration

```csharp
// ❌ Deux ensembles disjoints, dédoublonnés quand même (1 M d'identifiants au total)
await Mesurer("AVANT : Union", async db =>
    await db.Species.Where(s => s.IsSentient).Select(s => s.Id)
        .Union(db.Species.Where(s => !s.IsSentient).Select(s => s.Id)).CountAsync(), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : Concat", async db =>
    await db.Species.Where(s => s.IsSentient).Select(s => s.Id)
        .Concat(db.Species.Where(s => !s.IsSentient).Select(s => s.Id)).CountAsync(), afficherSql: true);
// ✔✔ Et ici, mieux encore : un seul Count, sans union du tout
await Mesurer("Sans union", db => db.Species.CountAsync());
```
**À observer** : `UNION` / `UNION ALL` ; l'opérateur de dédoublonnage dans le plan ; le résultat identique (les ensembles sont disjoints).

### Sans index / avec index et procédure stockée

Un index sur `IsSentient` ne supprime pas le dédoublonnage de l'`Union`. Procédure : sans objet.

### 🧪 TP 6.9 — « Disjoints ou pas ? »

*Énoncé.* Fusionnez les identifiants des vaisseaux opérationnels de la société 1 à 5 000 et ceux de capacité > 100. Les deux ensembles se recoupent-ils ? Écrivez-le avec `Union`, avec `Concat` + exclusion, avec un seul `Where ... ||`.

<details><summary>Correction</summary>

Ils se recoupent : `Concat` seul **compterait deux fois** les vaisseaux dans les deux ensembles ; il faut `Union`, ou `Concat` avec une condition d'exclusion sur le second ensemble, ou — le plus simple — `Where(s => (s.IsOperational && s.CompanyId <= 5_000) || s.CargoCapacity > 100)`.
</details>

---

## 6.10 Lire le SQL généré

**Axe concerné : outillage — premier réflexe de diagnostic.**

### La technique

Avant de mesurer, **lire** : `ToQueryString()`, journal, plan (voir 2.1). La plupart des problèmes sont visibles dans le texte du SQL (pas de `WHERE`, `OR … IS NULL`, `CONVERT_IMPLICIT`, jointures multiples, `SELECT` de toutes les colonnes).

### Dans quels cas on est concerné

Toute requête lente ou jugée « bizarre ».

### Exemple sans l'amélioration

```csharp
// ❌ « Ça marche » : on ne regarde pas
var l = await db.Starships.Where(s => s.Company.Name.Contains("Freight") || s.Model == null).Include(s => s.StarshipCargos).ToListAsync();
```

### Exemple avec l'amélioration

```csharp
// ✔ On lit d'abord
var q = db.Starships.TagWith("6.10-diagnostic")
        .Where(s => s.Company.Name.Contains("Freight") || s.Model == null).Include(s => s.StarshipCargos);
Console.WriteLine(q.ToQueryString());
```
**Grille de lecture** (cochez dans le SQL affiché) :

| Je cherche… | Signification |
|---|---|
| Absence de `WHERE` | filtre appliqué en mémoire (0.1) |
| `LIKE N'%…%'` | non sargable (2.8) |
| `OR … IS NULL` | sémantique C# des null (2.7) |
| plusieurs `LEFT JOIN` de collections | explosion cartésienne (3.7) |
| `SELECT` de toutes les colonnes | projection manquante (6.1) |
| `CONVERT_IMPLICIT` dans le plan | types incohérents (2.10) |
| `Sort` / `Scan` dans le plan | index manquant (6.7) |

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 6.10 — « Trois requêtes à diagnostiquer »

*Énoncé.* Pour chacune, dites ce que le SQL révèle **sans l'exécuter** : (a) `db.Starships.Where(s => s.Name.ToLower().Contains("x"))`, (b) `db.Companies.Include(c => c.Starships).Include(c => c.Trades)`, (c) `db.Species.Where(s => Normaliser(s.Name) == "x")` avec `Normaliser` une méthode locale.

<details><summary>Correction</summary>

(a) `LOWER([Name]) LIKE N'%x%'` : non sargable à double titre ; (b) deux collections : `LEFT JOIN` ×2, produit cartésien → split ou projection ; (c) exception de traduction (la méthode locale n'est pas traduisible dans un `Where`) → réécrire avec une fonction traduite ou filtrer après `AsEnumerable()` **sur un jeu déjà réduit**.
</details>

---

## 6.11 Taguer les requêtes

**Axe concerné : outillage de production.**

### La technique

`TagWith("contexte")` ajoute un commentaire en tête du SQL ; `TagWithCallSite()` ajoute fichier et ligne. Dans les DMV et le **Query Store**, on retrouve **quelle requête du code** consomme le plus.

### Dans quels cas on est concerné

Applications en production : des centaines de requêtes anonymes dans les statistiques sinon.

### Exemple sans l'amélioration

```csharp
await db.Starships.Where(s => s.CompanyId == 5).ToListAsync();     // dans les DMV : « SELECT [s].[Id], … FROM [Starships] » parmi 400 autres
```

### Exemple avec l'amélioration

```csharp
await db.Starships.TagWith("API GET /vaisseaux?societe={id}").Where(s => s.CompanyId == 5).ToListAsync();
```
```sql
-- Les requêtes les plus coûteuses, regroupées par tag
SELECT TOP 10 SUBSTRING(st.text, 1, 80) AS debut, qs.execution_count,
       qs.total_logical_reads / qs.execution_count AS reads_moyens,
       qs.total_worker_time / qs.execution_count / 1000.0 AS cpu_ms_moyen
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE '%-- API GET%'
ORDER BY qs.total_worker_time DESC;
```
**Query Store** (historique persistant des requêtes et des plans) : `ALTER DATABASE StarWarsTrade SET QUERY_STORE = ON;` *(à désactiver après essai : `… = OFF`)* ; puis dans SSMS, « Query Store → Top Resource Consuming Queries ».

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 6.11 — « Qui consomme ? »

*Énoncé.* Exécutez 3 requêtes taguées `A`, `B`, `C` avec des coûts différents. Écrivez le SQL qui donne, **par tag**, exécutions / lectures / CPU, et classez-les.

<details><summary>Correction</summary>

Méthode : un `CASE WHEN st.text LIKE '%-- A%' THEN 'A' …` dans un `GROUP BY` sur `sys.dm_exec_query_stats` (voir le TP 2.1). Le classement par `SUM(total_worker_time)` désigne la requête à optimiser en premier.
</details>

---

## 6.12 Agréger côté serveur

**Axe concerné : 🗄️ SQL + 🧠 mémoire.**

### La technique

`Count`, `Sum`, `Average`, `Min`, `Max`, `GroupBy` + agrégats : **dans la requête**, pas après `ToList()` (voir 1.4, 1.5, 0.8).

### Dans quels cas on est concerné

Tableaux de bord, statistiques, « top N ».

### Exemple sans l'amélioration

```csharp
// ❌ 2 M de lignes d'influence ramenées pour calculer 5 moyennes
await Mesurer("AVANT : agrégat en mémoire", async db =>
{
    var l = await db.FactionPlanets.AsNoTracking().Select(f => new { f.FactionId, f.Influence }).ToListAsync();
    var top = l.GroupBy(x => x.FactionId).Select(g => new { g.Key, Moy = g.Average(x => x.Influence) }).OrderByDescending(x => x.Moy).Take(5).ToList();
    return $"n°1 = {top[0].Key} ({top[0].Moy:N2})";
});
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : agrégat SQL", async db =>
{
    var top = await db.FactionPlanets.GroupBy(f => f.FactionId).Select(g => new { g.Key, Moy = g.Average(x => x.Influence) })
                      .OrderByDescending(x => x.Moy).Take(5).ToListAsync();
    return $"n°1 = {top[0].Key} ({top[0].Moy:N2})";
}, afficherSql: true);
```
**À observer** : lignes lues (2 M → 5) ; allocations ; temps.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
CREATE INDEX IX_Lab_FactionPlanets_Influence ON dbo.FactionPlanets (FactionId) INCLUDE (Influence);
-- relancer la requête : Stream Aggregate sur un index étroit et trié (pas de hachage)
DROP INDEX IX_Lab_FactionPlanets_Influence ON dbo.FactionPlanets;
```
(Remarque : `FactionPlanets` a une clé primaire composite `(FactionId, PlanetId)` : l'agrégat par `FactionId` est déjà **trié** par la clé ; l'index apporte surtout un **étroitissement** — comparez les lectures.)

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_TopFactions AS BEGIN SET NOCOUNT ON;
  SELECT TOP (5) FactionId, AVG(Influence) AS Moy FROM dbo.FactionPlanets GROUP BY FactionId ORDER BY AVG(Influence) DESC; END
```
Même plan que le `GroupBy` LINQ.

### 🧪 TP 6.12 — « Un seul passage »

*Énoncé.* Calculez en **une** requête, sur `FactionPlanets` : nombre de lignes, influence moyenne, influence max, nombre de capitales (`IsCapital`). Comparez à 4 requêtes séparées.

<details><summary>Correction</summary>

```csharp
var r = await db.FactionPlanets.GroupBy(_ => 1).Select(g => new { N = g.Count(), Moy = g.Average(f => f.Influence), Max = g.Max(f => f.Influence), Capitales = g.Count(f => f.IsCapital) }).SingleAsync();
```
Une seule lecture de la table (au lieu de 4).
</details>

---

## 6.13 Ne pas `Include` ce qu'un `Select` suffit à fournir

**Axe concerné : 🗄️ SQL (colonnes dupliquées) + 🧠 mémoire.**

### La technique

`Include` ramène **toutes** les colonnes de l'entité liée **et** la suit. Si l'on n'affiche qu'un champ de l'entité liée, naviguer dans un `Select` (voir 3.1, 3.3).

### Dans quels cas on est concerné

Affichage d'un « nom de parent » à côté d'une liste.

### Exemple sans l'amélioration

```csharp
// ❌ Toute la société, pour afficher son nom
await MesurerReseau("AVANT : Include", async db =>
    (await db.Starships.Where(s => s.Id <= 200_000).Include(s => s.Company).ToListAsync()).Select(s => (s.Name, s.Company.Name)).Count());
```

### Exemple avec l'amélioration

```csharp
await MesurerReseau("APRÈS : Select", async db =>
    (await db.Starships.Where(s => s.Id <= 200_000).Select(s => new { s.Name, Societe = s.Company.Name }).ToListAsync()).Count);
```
**À observer** : colonnes du `SELECT` (8 + 7 → 2) ; Mo reçus ; entités suivies (400 000 → 0).

### Sans index / avec index

Même idée que 6.1 : la projection autorise un index couvrant ; `Include` non.

### Comparaison avec une procédure stockée

Une procédure avec `JOIN` et seulement les deux colonnes produit le même plan que le `Select` LINQ.

### 🧪 TP 6.13 — « Combien d'octets pour un nom ? »

*Énoncé.* Mesurez les Mo reçus pour 200 000 vaisseaux avec (a) `Include(Company)` (b) `Select` du nom. Quel facteur obtient-on ? Qu'est-ce qui l'explique ?

<details><summary>Correction</summary>

Le facteur correspond au rapport entre la largeur des colonnes ramenées (toutes les colonnes de `Starship` + `Company` contre 2 chaînes). `Include` répète en outre les colonnes de la société sur chaque vaisseau d'une même société. Aucun chiffre n'est donné ici : c'est **votre mesure** qui fait foi.
</details>

---

## 6.14 Le coût des filtres globaux

**Axe concerné : 🗄️ SQL (prédicat ajouté partout).**

### La technique

Un `HasQueryFilter` est ajouté à **toutes** les requêtes de l'entité. Vérifier son coût (colonne indexée ? jointure ?) et prévoir `IgnoreQueryFilters()` pour les traitements d'administration (voir 3.12).

### Dans quels cas on est concerné

Suppression logique, multi-locataire.

### Exemple sans l'amélioration

Contexte dédié avec un filtre **par navigation** :

```csharp
public class LabFiltreCtx(DbContextOptions<StarWarsContext> o) : StarWarsContext(o)
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<Starship>().HasQueryFilter(s => s.Company.Capital > 0);         // jointure systématique vers Companies
    }
}
```
```csharp
await using var f = new LabFiltreCtx(new DbContextOptionsBuilder<StarWarsContext>().UseSqlServer(Cs).Options);
Console.WriteLine(f.Starships.Where(s => s.Id == 12345).ToQueryString());           // un INNER JOIN a été ajouté
```

### Exemple avec l'amélioration

```csharp
// ✔ Filtre sur une colonne de la table elle-même
//   b.Entity<Starship>().HasQueryFilter(s => s.IsOperational);
// ✔ Désactivation pour un traitement d'administration
await f.Starships.IgnoreQueryFilters().CountAsync();
```
**À observer** : le SQL de lecture par clé **avec** et **sans** filtre ; le plan (jointure ajoutée) ; l'effet sur un `Count`.

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Starships_Op ON dbo.Starships (CompanyId) INCLUDE (Name, Model) WHERE IsOperational = 1;     -- index filtré aligné sur le filtre global
DROP INDEX IX_Lab_Starships_Op ON dbo.Starships;
```

### Comparaison avec une procédure stockée

Une **vue** portant le filtre dans la base (`CREATE VIEW … WHERE IsOperational = 1`) applique la règle à tous les clients ; le filtre LINQ ne s'applique qu'aux requêtes EF.

### 🧪 TP 6.14 — « Filtrer sans jointure »

*Énoncé.* Passez d'un filtre global via navigation à un filtre sur une colonne locale. Comparez le SQL d'une lecture par clé et d'un `Count`, avec et sans l'index filtré.

<details><summary>Correction</summary>

Le filtre local supprime la jointure ; l'index filtré rend le `Count` du sous-ensemble opérationnel plus économique. Pour un filtre qui **doit** passer par une table liée (multi-locataire via secteur), dénormaliser la colonne de filtrage dans la table (`TenantId`) est généralement payant.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Starships_Cover          ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Starships_Cargo          ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Starships_Op             ON dbo.Starships;
DROP INDEX IF EXISTS IX_Lab_Species_Lifespan         ON dbo.Species;
DROP INDEX IF EXISTS IX_Lab_Species_Sentient_Height  ON dbo.Species;
DROP INDEX IF EXISTS IX_Lab_Markets_Kind_Date        ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants;
DROP INDEX IF EXISTS IX_Lab_Companies_FoundedOn      ON dbo.Companies;
DROP INDEX IF EXISTS IX_Lab_FactionPlanets_Influence ON dbo.FactionPlanets;

DROP PROCEDURE IF EXISTS dbo.Lab_VaisseauxActifs;
DROP PROCEDURE IF EXISTS dbo.Lab_PageMarches;
DROP PROCEDURE IF EXISTS dbo.Lab_TopFactions;

-- Si le Query Store a été activé pour 6.11 :
-- ALTER DATABASE StarWarsTrade SET QUERY_STORE = OFF;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
