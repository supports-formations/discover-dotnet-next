# 09 — Nouveautés d'EF Core qui comptent pour les performances

> Suite de [`08-volumetrie.md`](08-volumetrie.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans, `ToQueryString`).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> **Sources.** Les fonctionnalités ci-dessous ont été **recoupées avec la documentation officielle d'EF Core** (pages « What's new in EF Core 8 / 9 / 10 » et « Breaking changes ») au moment de la rédaction. Quand une affirmation vient de ma connaissance générale **sans** avoir été recoupée, elle est marquée *(non recoupé)*. **EF Core 11** (la version de votre projet est `11.0.0`) : **je n'ai pas pu recouper ses nouveautés** ; consultez la page « What's new in EF Core 11 » avant l'atelier (voir 9.10).
> Index et objets SQL : `IX_Lab_` / `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

| # | Nouveauté | Version | Axe |
|---|---|---|---|
| [9.1](#91-carte-des-versions) | Carte des versions | 5 → 11 | — |
| [9.2](#92-requêtes-fractionnées-et-include-filtré) | Requêtes fractionnées, `Include` filtré | 5 *(non recoupé)* | SQL |
| [9.3](#93-executeupdate--executedelete-et-leurs-évolutions) | `ExecuteUpdate` / `ExecuteDelete` et évolutions | 7 *(non recoupé)* → 9 → 10 | SQL + .NET |
| [9.4](#94-collections-paramétrées-contains) | `Contains` : `OPENJSON`, paramètres multiples, `EF.Constant/Parameter/MultipleParameters` | 8 → 10 | SQL |
| [9.5](#95-json-collections-primitives-et-types-complexes) | JSON, collections primitives, types complexes | 8 → 10 | SQL + .NET |
| [9.6](#96-améliorations-de-traduction-count--0--exists-sqlquery) | Traductions améliorées (`Count != 0` → `EXISTS`, `SqlQuery`) | 8 / 9 | SQL |
| [9.7](#97-leftjoin-et-rightjoin) | `LeftJoin` / `RightJoin` | 10 | lisibilité |
| [9.8](#98-filtres-de-requête-nommés) | Filtres de requête nommés | 10 | SQL + maintenance |
| [9.9](#99-requêtes-précompilées-et-nativeaot) | Requêtes précompilées / NativeAOT | 9 (expérimental) → 10 | démarrage |
| [9.10](#910-recherche-vectorielle-et-type-json-natif-sql-server-2025) | Recherche vectorielle, type `json` natif | 10 | SQL |
| [9.11](#911-ef-core-11) | EF Core 11 | 11 | à vérifier |
| [9.12](#912-migrations-bundle-et-batching-de-savechanges) | Migrations *bundle*, *batching* | 6 / tous | déploiement |

---

## 9.1 Carte des versions

| Version | Ce qui compte pour ce workshop | Recoupé ? |
|---|---|---|
| EF 5 | `AsSplitQuery`, `Include` filtré | non |
| EF 6 | migrations *bundle*, tables temporelles | non |
| EF 7 | `ExecuteUpdate` / `ExecuteDelete`, `SqlQuery<T>` pour les scalaires | non |
| **EF 8** | `Contains` → paramètre JSON + `OPENJSON` ; collections primitives mappées en JSON par convention ; `SqlQuery<T>` pour les types non mappés ; types complexes | **oui** (sauf types complexes) |
| **EF 9** | `GroupBy` et `ExecuteUpdate` sur les types complexes ; `Count != 0` → `EXISTS` ; NativeAOT et requêtes précompilées (**expérimental**) | **oui** |
| **EF 10** | `LeftJoin` / `RightJoin` ; filtres de requête nommés ; `ExecuteUpdate` avec lambda ordinaire ; collections paramétrées : **paramètres scalaires multiples par défaut** ; `ComplexProperty(...).ToJson()` ; `ExecuteUpdate` sur colonnes JSON ; type `json` natif ; recherche **vectorielle** (SQL Server 2025 / Azure SQL) ; `UseCompatibilityLevel(…)` | **oui** |
| EF 11 | **non recoupé** | non |

> **Piège de communication** : une même syntaxe peut produire un **SQL différent selon la version** (ex. `Contains`). Quand vous présentez une mesure, indiquez toujours la version d'EF Core.

### Comment comparer deux versions (laboratoire multi-versions)

Pour **voir** la différence entre deux versions sans changer votre projet principal, créez un second projet console avec une autre version du paquet :

```bash
dotnet new console -n Lab8 && cd Lab8
dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.*
# copiez Models/ et Data/ du projet principal (adaptez les namespaces)
```
Écrivez la **même requête** dans les deux projets et comparez `ToQueryString()` et les plans. C'est la méthode la plus fiable pour distinguer ce qui vient d'EF de ce qui vient du serveur.

---

## 9.2 Requêtes fractionnées et `Include` filtré

**Version : EF 5 *(non recoupé)*. Axe : 🗄️ SQL.** Déjà traité au chapitre **3** (3.2 et 3.6) : retenez que `AsSplitQuery` échange des allers-retours contre un volume moindre, et que l'`Include` filtré ne charge qu'une partie d'une collection.

### Exemple (rappel avec/sans)

```csharp
await MesurerReseau("AVANT : 2 collections en une requête", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSingleQuery().Include(g => g.Trades).Include(g => g.MarketGoods).ToListAsync()).Count);
await MesurerReseau("APRÈS : AsSplitQuery", async db =>
    (await db.Goods.AsNoTracking().Where(g => g.Id <= 2_000).AsSplitQuery().Include(g => g.Trades).Include(g => g.MarketGoods).ToListAsync()).Count);
```
Index, procédure stockée, TP : voir 3.2.

---

## 9.3 `ExecuteUpdate` / `ExecuteDelete` et leurs évolutions

**Versions : EF 7 *(non recoupé)* ; EF 9 : types complexes ; EF 10 : lambda ordinaire et colonnes JSON. Axe : 🗄️ SQL + ⚙️ .NET.**

### La notion

Après l'introduction d'`ExecuteUpdate` (chapitre 5), deux évolutions facilitent son usage :
- **EF 9** : `ExecuteUpdate` accepte les **propriétés de type complexe** (en listant chaque membre) et `GroupBy` fonctionne sur un type complexe.
- **EF 10** : `ExecuteUpdate` accepte une **lambda ordinaire** (non-expression) : on peut écrire des `if` pour des setters **conditionnels** sans construire d'arbre d'expression ; et il met à jour les propriétés d'une **colonne JSON** mappée en `ComplexProperty(...).ToJson()` (sur SQL Server 2025, via la fonction `.modify` ; sinon via `JSON_MODIFY`).

### Dans quels cas on est concerné

Mises à jour partielles pilotées par des paramètres ; compteurs ou valeurs dans du JSON.

### Exemple sans l'amélioration

```csharp
// ❌ Avant EF 10 : un arbre d'expression construit à la main pour des setters facultatifs, ou autant d'instructions que de cas
Task<int> Ajuster(StarWarsContext db, decimal? facteurPrix, decimal? deltaPoids)
{
    if (facteurPrix is not null && deltaPoids is not null)
        return db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * facteurPrix.Value).SetProperty(g => g.WeightKg, g => g.WeightKg + deltaPoids.Value));
    if (facteurPrix is not null)
        return db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * facteurPrix.Value));
    // … 2ᵉ, 3ᵉ cas : combinatoire
    return Task.FromResult(0);
}
```

### Exemple avec l'amélioration

```csharp
// ✔ EF 10 : lambda ordinaire (documentation officielle : « ExecuteUpdateAsync accepte désormais une lambda non-expression »)
await Mesurer("APRÈS : setters conditionnels", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.10m);
            if (deltaPoidsFourni) s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1);
        })), afficherSql: true);
```
**À observer** : le SQL ne contient **que** les colonnes dont le setter a été appliqué ; le nombre de variantes de requête (un seul `ExecuteUpdate` écrit) ; si `deltaPoidsFourni` change, deux **formes** de SQL → deux plans.

**JSON (EF 10)** : avec `modelBuilder.Entity<Blog>().ComplexProperty(b => b.Details, d => d.ToJson())`, la documentation montre :
```csharp
await context.Blogs.ExecuteUpdateAsync(s => s.SetProperty(b => b.Details.Views, b => b.Details.Views + 1));
```
Testez sur la table `Lab_Doc` du chapitre 2.11 en remplaçant `OwnsOne(...).ToJson()` par `ComplexProperty(...).ToJson()` : regardez le SQL (`JSON_MODIFY` sur nvarchar(max) ou `.modify` sur type `json`).

### Sans index / avec index

Mêmes remarques qu'en 5.1 : chaque index qui contient une colonne modifiée coûte à l'écriture. Une mise à jour **dans du JSON** n'utilise pas d'index sur la propriété JSON, sauf colonne calculée indexée (2.11).

### Comparaison avec une procédure stockée

Les setters conditionnels EF 10 évitent d'écrire des procédures « une par combinaison » ou du SQL dynamique : c'est le cas où LINQ **a rattrapé** l'expressivité d'une procédure à paramètres optionnels.

### 🧪 TP 9.3 — « Setters à la carte »

*Énoncé.* Écrivez `AjusterBiens(db, decimal? facteurPrix, decimal? deltaPoids)` avec un seul `ExecuteUpdate` à lambda ordinaire. Vérifiez le SQL pour les trois combinaisons et le comportement quand les deux arguments sont nuls.

<details><summary>Correction</summary>

```csharp
static Task<int> AjusterBiens(StarWarsContext db, decimal? facteurPrix, decimal? deltaPoids) =>
    db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s =>
    {
        if (facteurPrix is { } f) s.SetProperty(g => g.BasePrice, g => g.BasePrice * f);
        if (deltaPoids is { } d)  s.SetProperty(g => g.WeightKg, g => g.WeightKg + d);
    });
```
Avec deux arguments nuls, aucun setter n'est appliqué : vérifiez ce que fait votre version (exception ou instruction vide) avant de l'exposer dans une API.
</details>

---

## 9.4 Collections paramétrées (`Contains`)

**Versions : EF 8 (`OPENJSON`) → EF 10 (paramètres multiples par défaut). Axe : 🗄️ SQL.**

### La notion

La traduction de `liste.Contains(x.Id)` a changé **deux fois** :

| Version | Traduction par défaut | SQL (SQL Server) |
|---|---|---|
| ≤ EF 7 | valeurs **inlinées** (constantes) | `IN (1, 2, 3)` |
| **EF 8-9** | **un paramètre JSON** lu par `OPENJSON` | `IN (SELECT value FROM OPENJSON(@ids) …)` (exige un niveau de compatibilité ≥ 130) |
| **EF 10** | **plusieurs paramètres scalaires** | `IN (@ids1, @ids2, @ids3)` |

Contrôle, **par requête** : `EF.Constant(liste)`, `EF.Parameter(liste)` (JSON), `EF.MultipleParameters(liste)` ; **globalement** : `UseSqlServer(Cs, o => o.UseParameterizedCollectionMode(ParameterTranslationMode.Constant | Parameter | MultipleParameters))`.

### Dans quels cas on est concerné

Mise à jour d'EF Core (le **même code** change de SQL, donc de plan et de mémoire de plans) ; listes de tailles variables.

### Exemple sans l'amélioration

```csharp
var ids = Enumerable.Range(1, 10).ToList();
// Le mode courant selon votre version : regardez ce que génère chaque variante
Console.WriteLine(db.Goods.Where(g => ids.Contains(g.Id)).ToQueryString());
```

### Exemple avec l'amélioration

```csharp
Console.WriteLine(db.Goods.Where(g => EF.Constant(ids).Contains(g.Id)).ToQueryString());            // valeurs inlinées
Console.WriteLine(db.Goods.Where(g => EF.Parameter(ids).Contains(g.Id)).ToQueryString());           // un paramètre JSON + OPENJSON
Console.WriteLine(db.Goods.Where(g => EF.MultipleParameters(ids).Contains(g.Id)).ToQueryString());  // un paramètre par valeur
```
**À observer** : la **forme** du SQL pour 10, 100, 1 000 éléments ; le **nombre de plans en cache** après 4 tailles différentes (requête du 0.4/2.5) ; l'**estimation de lignes** dans le plan : avec `OPENJSON`, SQL Server estime souvent mal la taille de la liste ; avec des constantes, le plan est le meilleur mais chaque combinaison de valeurs crée un plan ; avec des paramètres multiples, le plan dépend du **nombre** de paramètres.

### Sans index / avec index

`Goods.Id` est la clé : seek par valeur dans tous les modes. Sur une colonne non indexée, chaque mode scanne ; l'index change le plan d'une **jointure** (le plan avec `OPENJSON` privilégie souvent un *Nested Loops* par valeur).

### Comparaison avec une procédure stockée

Une procédure à paramètre table (TVP, voir 7.4.6) a un plan **unique** quelle que soit la taille de la liste ; c'est la référence pour les très grandes listes.

### 🧪 TP 9.4 — « Après la montée de version »

*Énoncé.* Votre application passe d'EF 8 à EF 10 sans changer une ligne. Pour une API qui reçoit 5, 50 ou 500 identifiants, comparez dans le laboratoire multi-versions (9.1) : SQL, nombre de plans, temps. Quelle option globale choisissez-vous pour garder le comportement antérieur ?

<details><summary>Correction</summary>

Pour retrouver le comportement EF 8-9 : `UseParameterizedCollectionMode(ParameterTranslationMode.Parameter)` (paramètre JSON). Mesurez avant de décider : paramètres multiples (défaut EF 10) donnent de bons plans pour de petites listes mais multiplient les plans selon la taille ; le mode JSON a un plan unique mais des estimations moins précises.
</details>

---

## 9.5 JSON, collections primitives et types complexes

**Versions : EF 8 (collections primitives, JSON) ; EF 9 (types complexes dans `GroupBy`/`ExecuteUpdate`) ; EF 10 (`ComplexProperty(...).ToJson()`, type `json` natif). Axe : 🗄️ SQL + ⚙️ .NET.**

### La notion

- **Collections primitives** (`List<int>`, `string[]`…) : mappées **par convention** en colonne JSON (EF 8).
- **Types complexes** (`ComplexProperty`) : objets valeur sans identité ; **EF 9** : `GroupBy` sur un type complexe et `ExecuteUpdate` (en listant chaque membre) ; **EF 10** : un type complexe peut être mappé en **colonne JSON** (`ToJson()`), et `ExecuteUpdate` modifie ses propriétés.
- **Type `json` natif** (SQL Server 2025) : EF 10 génère `json` au lieu de `nvarchar(max)` quand le niveau de compatibilité cible est suffisant (`o.UseCompatibilityLevel(170)`).

### Dans quels cas on est concerné

Données semi-structurées, listes de valeurs, objets valeur à grande échelle.

### Exemple sans l'amélioration

```csharp
// ❌ OwnsOne + ToJson : l'objet possédé est une entité à part entière dans le suivi (voir 2.11-2.12)
await using var c1 = new LabJsonContext(opt);              // contexte du chapitre 2.11
var l1 = await c1.Docs.ToListAsync();
Console.WriteLine($"suivies : {c1.ChangeTracker.Entries().Count():N0}");
```

### Exemple avec l'amélioration

```csharp
// ✔ ComplexProperty + ToJson (EF 10) : pas d'entité possédée, une seule entrée de suivi par ligne
//    modelBuilder.Entity<LabDoc>().ComplexProperty(x => x.Details, d => d.ToJson());
await using var c2 = new LabJsonContextComplexe(opt);
var l2 = await c2.Docs.ToListAsync();
Console.WriteLine($"suivies : {c2.ChangeTracker.Entries().Count():N0}");
```
**À observer** : le nombre d'entrées de suivi ; le SQL de lecture (identique) ; le SQL de `ExecuteUpdate` sur une propriété JSON ; le type de colonne (`nvarchar(max)` sur SQL Server 2022).

### Sans index / avec index

Comme au 2.11 : filtrer sur une propriété JSON scanne ; une **colonne calculée** indexée permet le seek. Rien de plus magique avec EF 10 : la nouveauté est le **mappage**, pas l'indexation.

### Comparaison avec une procédure stockée

`OPENJSON … WITH (…)` dans une procédure projette plusieurs propriétés JSON en une passe ; EF génère un SQL équivalent pour les collections.

### 🧪 TP 9.5 — « Possédé ou complexe ? »

*Énoncé.* Mesurez le chargement de 200 000 documents avec `OwnsOne(...).ToJson()` et `ComplexProperty(...).ToJson()` : entrées de suivi, Mo alloués, temps. Écrivez le `ExecuteUpdate` qui incrémente une propriété JSON dans chaque cas et dites ce qui est possible ou non.

<details><summary>Correction</summary>

La documentation indique que `ExecuteUpdate` sur JSON requiert un **type complexe** (`ComplexProperty`), pas un type possédé : le cas `OwnsOne` ne permet pas ce `ExecuteUpdate`. Les entrées de suivi sont plus nombreuses avec `OwnsOne`.
</details>

---

## 9.6 Améliorations de traduction (`Count != 0` → `EXISTS`, `SqlQuery`)

**Versions : EF 8 (`SqlQuery<T>` sur types non mappés) ; EF 9 (`Count != 0` → `EXISTS`). Axe : 🗄️ SQL.**

### La notion

- **EF 9** : un prédicat `collection.Count != 0` **dans un `Where`** est désormais traduit en **`EXISTS`** (au lieu de `COUNT(*)`), plus efficace. **Nuance importante** : cela concerne **la navigation de collection à l'intérieur de la requête** (`b.Posts.Count != 0`), **pas** un `await q.CountAsync() > 0` évalué côté client : ce dernier reste un `COUNT(*)` (voir 1.3, 6.4). Écrire `Any()` reste la règle.
- **EF 8** : `Database.SqlQuery<T>` fonctionne pour des **types non mappés** (DTO) et des scalaires.

### Dans quels cas on est concerné

Code existant écrit avec `Count() != 0` / `Count > 0` sur des navigations ; bascule de version.

### Exemple sans l'amélioration

```csharp
// Navigation Count != 0 : selon la version, COUNT(*) (EF ≤ 8) ou EXISTS (EF ≥ 9)
Console.WriteLine(db.Planets.Where(p => p.Markets.Count != 0).ToQueryString());
// ❌ Terminal Count côté client : reste un COUNT(*) dans toutes les versions
await Mesurer("Count terminal", async db => await db.Markets.CountAsync(m => m.IsBlackMarket) > 0, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Any : EXISTS dans toutes les versions
await Mesurer("Any", db => db.Markets.AnyAsync(m => m.IsBlackMarket), afficherSql: true);
// ✔ Comparaison navigation : Count != 0 (EF 9+) vs Any()
Console.WriteLine(db.Planets.Where(p => p.Markets.Any()).ToQueryString());
```
**À observer** : sur EF 11, le SQL de `p.Markets.Count != 0` contient `EXISTS` ; **dans le laboratoire EF 8** (9.1), `COUNT(*)` ; les deux écritures (`Count != 0` et `Any()`) donnent le **même** SQL en EF ≥ 9.

### Sans index / avec index

`IX_Markets_PlanetId` (FK) sert l'`EXISTS` ; sans lui, le `COUNT(*)` ou l'`EXISTS` scannent.

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 9.6 — « Que gagne-t-on en montant de version ? »

*Énoncé.* Écrivez `Planets.Where(p => p.Markets.Count != 0)` dans le laboratoire EF 8 et dans le projet EF 11. Comparez SQL, plan et *logical reads* sur 3 M de planètes.

<details><summary>Correction</summary>

EF 8 : un `COUNT(*)` corrélé par planète ; EF ≥ 9 : un `EXISTS` (semi-jointure). Le plan est souvent identique après optimisation par SQL Server ; l'écart se lit dans la **complexité du SQL** et, selon les cas, dans les lectures.
</details>

---

## 9.7 `LeftJoin` et `RightJoin`

**Version : EF 10 (avec .NET 10). Axe : lisibilité, risque d'erreur (pas de gain de vitesse attendu).**

### La notion

.NET 10 ajoute les opérateurs LINQ `LeftJoin` et `RightJoin` ; EF Core 10 les traduit en `LEFT JOIN` / `RIGHT JOIN`. Ils remplacent le motif `GroupJoin` + `SelectMany` + `DefaultIfEmpty`. **La syntaxe de requête C# (`from … select`) ne les gère pas encore.**

### Dans quels cas on est concerné

Toute jointure externe.

### Exemple sans l'amélioration

```csharp
// ❌ Le motif historique
await Mesurer("AVANT : GroupJoin + DefaultIfEmpty", async db =>
    (await db.Planets.Where(p => p.Id <= 300_000)
        .GroupJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, ms) => new { p, ms })
        .SelectMany(x => x.ms.DefaultIfEmpty(), (x, m) => new { x.p.Name, Marche = m == null ? null : m.Name }).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ LeftJoin natif
await Mesurer("APRÈS : LeftJoin", async db =>
    (await db.Planets.Where(p => p.Id <= 300_000)
        .LeftJoin(db.Markets, p => p.Id, m => m.PlanetId, (p, m) => new { p.Name, Marche = m == null ? null : m.Name }).ToListAsync()).Count, afficherSql: true);
```
**À observer** : **les deux SQL sont-ils identiques ?** (comparez le texte) ; les deux plans ; le nombre de lignes de code et de risques d'erreur (oubli du `DefaultIfEmpty`, qui transforme le left join en inner join).

### Sans index / avec index

Les mêmes que pour toute jointure (index de FK `IX_Markets_PlanetId`).

### Comparaison avec une procédure stockée

Sans objet : c'est la même instruction `LEFT JOIN`.

### 🧪 TP 9.7 — « L'inner join involontaire »

*Énoncé.* Reproduisez l'erreur classique : un `GroupJoin` + `SelectMany` **sans** `DefaultIfEmpty`. Quel est le résultat ? Montrez qu'avec `LeftJoin` l'erreur n'est plus possible.

<details><summary>Correction</summary>

Sans `DefaultIfEmpty`, les planètes sans marché disparaissent (c'est un `INNER JOIN`). Avec `LeftJoin`, l'intention (conserver les lignes du côté gauche) est dans le nom de l'opérateur.
</details>

---

## 9.8 Filtres de requête nommés

**Version : EF 10. Axe : 🗄️ SQL + maintenance.**

### La notion

Avant EF 10, une entité ne pouvait avoir **qu'un** filtre global ; `IgnoreQueryFilters()` les désactivait **tous**. EF 10 permet de **nommer** plusieurs filtres sur une même entité et d'en désactiver **sélectivement**.

```csharp
modelBuilder.Entity<Blog>()
    .HasQueryFilter("SoftDeletionFilter", b => !b.IsDeleted)
    .HasQueryFilter("TenantFilter", b => b.TenantId == tenantId);
```
*(Documentation officielle.)* La désactivation sélective s'exprime par `IgnoreQueryFilters(["SoftDeletionFilter"])` *(vérifiez la signature exacte dans votre version)*.

### Dans quels cas on est concerné

Suppression logique **et** multi-locataire : un traitement d'administration doit voir les lignes supprimées mais rester cloisonné par locataire.

### Exemple sans l'amélioration

```csharp
// ❌ Un seul filtre combiné : pour voir les lignes « supprimées » il faut tout désactiver, y compris l'isolation par locataire
modelBuilder.Entity<Market>().HasQueryFilter(m => !m.IsBlackMarket && m.PlanetId <= 1_000_000);
var tout = await db.Markets.IgnoreQueryFilters().CountAsync();             // plus aucun filtre, ni l'un ni l'autre
```

### Exemple avec l'amélioration

```csharp
modelBuilder.Entity<Market>()
    .HasQueryFilter("PasNoir", m => !m.IsBlackMarket)
    .HasQueryFilter("Locataire", m => m.PlanetId <= 1_000_000);
var vuAdmin = await db.Markets.IgnoreQueryFilters(["PasNoir"]).CountAsync();   // garde « Locataire »
```
**À observer** : `ToQueryString()` avec 0, 1, 2 filtres désactivés ; le plan (un prédicat par filtre ; **un filtre par navigation ajoute une jointure**, voir 3.12).

### Sans index / avec index

Un index filtré `WHERE IsBlackMarket = 0` aligné sur un filtre nommé (3.12) reste utile ; ne le créez que si ce filtre est **toujours** actif sur cette requête.

### Comparaison avec une procédure stockée

Une **vue** par filtre est l'équivalent SQL ; elle ne se désactive pas par requête.

### 🧪 TP 9.8 — « Deux filtres, trois vues »

*Énoncé.* Définissez deux filtres nommés, puis écrivez trois requêtes : vue utilisateur (les deux), vue administrateur du locataire (sans suppression logique), vue super-administrateur (aucun). Comparez les SQL.

<details><summary>Correction</summary>

Trois SQL : deux prédicats ; un prédicat (`Locataire`) ; aucun. Vérifiez qu'aucun chemin du code ne désactive `Locataire` par erreur : un test qui compte les lignes d'un autre locataire le garantit.
</details>

---

## 9.9 Requêtes précompilées et NativeAOT

**Versions : EF 9 (expérimental) → EF 10 (à vérifier). Axe : ⚙️ .NET (démarrage).**

### La notion

La **précompilation des requêtes** identifie **statiquement** les requêtes LINQ du programme et génère du code C# (des *intercepteurs*) contenant le **SQL final** et le code de matérialisation. Intérêt : **démarrage beaucoup plus rapide** (plus de traduction à l'exécution) et compatibilité **NativeAOT**. La documentation d'EF 9 précise que cette fonctionnalité est **« hautement expérimentale »** et **déconseillée en production**, avec de nombreuses limites ; elle annonce une stabilisation envisagée pour EF 10 : **vérifiez la page « NativeAOT and precompiled queries » de votre version** avant de présenter son statut. Côté mise en œuvre : activer la publication AOT, accepter l'usage des *intercepteurs C#* (option du projet) et référencer le paquet `Microsoft.EntityFrameworkCore.Tasks`, qui précompile les requêtes et génère le **modèle compilé** à la publication.

### Dans quels cas on est concerné

Outils CLI, fonctions serverless, conteneurs à démarrage fréquent.

### Exemple sans l'amélioration

```csharp
// Mesurer le démarrage d'un processus JIT (plusieurs lancements) : temps jusqu'à la première réponse
var t0 = Stopwatch.GetTimestamp();
await using var d = NewDb(); var n = await d.Sectors.CountAsync();
Console.WriteLine($"1ʳᵉ requête : {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:N0} ms");
```

### Exemple avec l'amélioration

Suivez la page officielle de votre version ; schéma général :
```bash
dotnet add package Microsoft.EntityFrameworkCore.Tasks
# activer PublishAot et l'opt-in des intercepteurs C# dans le .csproj, puis :
dotnet publish -c Release -r win-x64
```
Relancez le binaire AOT plusieurs fois et comparez la première requête, la taille du binaire, la mémoire au démarrage.

**À observer** : l'écart de la première requête ; les **requêtes refusées** par le générateur (dynamiques : 7.2) ; les avertissements de publication.

### Sans index / avec index et procédure stockée

Sans objet : c'est le démarrage .NET.

### 🧪 TP 9.9 — « Ce que l'AOT interdit »

*Énoncé.* Prenez trois requêtes de ce workshop : une statique, une construite avec des `Expression` (7.2.1), une `Include` conditionnel. Dites lesquelles la précompilation peut gérer et pourquoi.

<details><summary>Correction</summary>

Seules les requêtes dont la **forme est connue à la compilation** peuvent être précompilées ; les prédicats construits dynamiquement ne le peuvent pas. C'est le même compromis que pour les requêtes compilées (7.1.2), poussé plus loin.
</details>

---

## 9.10 Recherche vectorielle et type `json` natif (SQL Server 2025)

**Version : EF 10. Axe : 🗄️ SQL. Prérequis : SQL Server 2025 / Azure SQL — **non disponible sur votre conteneur SQL Server 2022**.**

### La notion

- **Recherche vectorielle** (embeddings, RAG) : propriété `SqlVector<float>` mappée sur une colonne `vector(N)` ; requête de similarité :
  ```csharp
  var topSimilaires = await context.Blogs
      .OrderBy(b => EF.Functions.VectorDistance("cosine", b.Embedding, vecteurRequete))
      .Take(3).ToListAsync();
  ```
  *(Documentation officielle.)* La page indique que le support vectoriel d'EF 10 n'est disponible **qu'avec SQL Server 2025 et supérieur** (et Azure SQL).
- **Type `json` natif** : colonnes `json` au lieu de `nvarchar(max)` ; EF 10 règle son SQL selon `UseCompatibilityLevel(170)` (cela ne modifie **pas** le niveau de compatibilité de la base elle-même).

### Dans quels cas on est concerné

Recherche sémantique, RAG ; stockage JSON volumineux.

### Exemple (à lire, non exécutable sur SQL Server 2022)

Exécutez-le sur une instance SQL Server 2025 ou Azure SQL **de test** ; sur votre conteneur 2022, la création de colonne `vector` échoue.

```csharp
public class Document
{
    public int Id { get; set; }
    public string Titre { get; set; } = "";
    [Column(TypeName = "vector(1536)")] public SqlVector<float> Embedding { get; set; }
}
```
**Comparez** (sur une instance compatible) : `ORDER BY VECTOR_DISTANCE(…)` + `TOP` **sans index vectoriel** (parcourt tous les vecteurs) et avec les index vectoriels proposés par la version du serveur *(à vérifier : disponibilité selon l'édition/aperçu)*.

### Sans index / avec index

C'est le cœur du sujet pour la recherche vectorielle : un balayage exact de millions de vecteurs est coûteux ; un index approché troque exactitude contre vitesse. Consultez la documentation SQL Server de votre version.

### Comparaison avec une procédure stockée

Aucune différence à plan égal : `VECTOR_DISTANCE` s'appelle de la même manière en T-SQL.

### 🧪 TP 9.10 — « Plan B sans SQL Server 2025 »

*Énoncé.* Pour un atelier sur SQL Server 2022, proposez deux manières de montrer la notion : (a) une instance 2025/Azure SQL **partagée par le formateur**, (b) un **extrait de code et de plan** préparés. Quelles limites présente (b) ?

<details><summary>Correction</summary>

(b) ne permet pas aux participants de **mesurer** ; il faut le dire explicitement. Alternative locale pour comprendre le coût d'un balayage exact : stocker des vecteurs dans une table de `float` et calculer la distance en SQL ou en .NET, en constatant le coût linéaire avec le nombre de lignes.
</details>

---

## 9.11 EF Core 11

**Version : 11. Statut : non recoupé.**

Votre projet référence `Microsoft.EntityFrameworkCore 11.0.0`. Je n'ai **pas retrouvé** de page « What's new in EF Core 11 » dans la documentation consultée : **ne présentez rien d'EF 11 sans l'avoir vérifié**. Procédure :

1. Lire la page officielle « What's new in EF Core 11 » et la liste des *breaking changes* d'EF 11.
2. Pour chaque nouveauté qui touche la performance : **tester** avec `ToQueryString()` et le laboratoire multi-versions (9.1), comparer avec EF 10.
3. Ajouter à ce chapitre une section « 9.x » avec le même gabarit (notion, sans/avec, index, procédure, TP).

---

## 9.12 Migrations *bundle* et *batching* de `SaveChanges`

**Versions : EF 6 (bundle) *(non recoupé)* ; *batching* : toutes versions. Axe : déploiement et allers-retours.**

Déjà traités en **5.4** (batching) et **5.8** (bundle de migrations). Pour mémoire, **« bundler de requêtes »** n'a pas d'équivalent EF unique : cela recouvre le **bundle de migrations**, le **batching** des écritures et l'envoi de plusieurs instructions par aller-retour. Voir la remarque de 5.8.

---

## Script de nettoyage

Ce chapitre ne crée pas d'objet SQL en propre ; reprenez celui du chapitre 2.11 si vous avez réutilisé `Lab_Doc` :

```sql
DROP TABLE IF EXISTS dbo.Lab_Doc;
DROP TABLE IF EXISTS dbo.Lab_Livraison;
-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
Si vous avez créé le projet `Lab8` (laboratoire multi-versions), il est indépendant de la base : supprimez simplement son dossier.
