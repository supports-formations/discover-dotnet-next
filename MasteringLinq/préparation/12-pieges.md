# 12 — Pièges classiques : la chasse au bug

> Suite de [`11-diagnostic.md`](11-diagnostic.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `ToQueryString`, `SET STATISTICS IO, TIME ON`) et le compteur de budget du [11](11-diagnostic.md).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> **Format de ce chapitre.** Chaque piège est présenté comme **un bug de production** : le **symptôme**, le **code fautif**, le **code corrigé**, **comment le détecter** (outil et signal), puis un **TP « chasse au bug »** avec correction. Les pièges déjà démontrés ailleurs sont rejoués sur **un autre scénario** ; le renvoi indique où lire la base. Les rubriques « index » et « procédure stockée » ne sont présentes que quand elles apportent quelque chose.
> Objets de démonstration : `IX_Lab_`, `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

| # | Piège | Famille |
|---|---|---|
| [12.1](#121-tolist--asenumerable-prématuré) | `ToList()` / `AsEnumerable()` prématuré | volume |
| [12.2](#122-capture-de-variable-et-exécution-différée) | Capture de variable + exécution différée | exactitude |
| [12.3](#123-double-énumération) | Double énumération | allers-retours |
| [12.4](#124-count--0-et-count-sur-grosse-table) | `Count() > 0`, `Count()` sur grosse table | SQL |
| [12.5](#125-include-multiples-et-explosion-cartésienne) | `Include` multiples → explosion cartésienne | volume |
| [12.6](#126-n1-lazy-loading-find-en-boucle) | N+1 (lazy loading, `Find` en boucle) | allers-retours |
| [12.7](#127-contains-avec-une-liste-énorme) | `Contains` avec une liste énorme | SQL |
| [12.8](#128-fonction-non-traduisible-dans-un-where) | Fonction non traduisible dans un `Where` | traduction |
| [12.9](#129-skiptake-sans-orderby-déterministe) | `Skip/Take` sans `OrderBy` déterministe | exactitude |
| [12.10](#1210-first--single--last--comportements) | `First` / `Single` / `Last` | exactitude |
| [12.11](#1211-collation-et-tolower-non-sargable) | Collation et `ToLower()` | SQL |
| [12.12](#1212-agrégats-sur-ensemble-vide) | `Sum`/`Average`/`Max` sur ensemble vide | exactitude |
| [12.13](#1213-datetimenow--client-ou-serveur) | `DateTime.Now` côté client ou serveur | exactitude |
| [12.14](#1214-dbcontext-partagé-entre-threads) | `DbContext` partagé entre threads | concurrence |
| [12.15](#1215-tracking-massif-oublié) | Tracking massif oublié | mémoire |
| [12.16](#1216-result--wait-sur-du-code-async) | `.Result` / `.Wait()` sur du code async | threads |
| [12.17](#1217-ienumerablet-en-paramètre-de-repository) | `IEnumerable<T>` dans les signatures | volume |
| [12.18](#1218-func-au-lieu-dexpression) | `Func<>` au lieu d'`Expression<Func<>>` | volume |
| [12.19](#1219-filtres-globaux-oubliés-ou-non-indexés) | Filtres globaux oubliés ou non indexés | exactitude + SQL |
| [12.20](#1220-constantes-variables-dans-les-requêtes) | Constantes variables dans les requêtes | cache |

Puis : [grille de détection](#grille-de-détection--du-symptôme-à-loutil) · [check-list de revue de code](#check-list-de-revue-de-code) · [script de nettoyage](#script-de-nettoyage).

---

## 12.1 `ToList()` / `AsEnumerable()` prématuré

**Symptôme** : une page lente ou un service qui consomme des Go de mémoire pour afficher quelques lignes. *Base : 0.1, 0.5, 6.3.*

**Code fautif**
```csharp
// Le plus riches des vaisseaux d'une société : on charge TOUS les vaisseaux de TOUTES les sociétés
var tous = await db.Starships.AsNoTracking().ToListAsync();
var l = tous.Where(s => s.CompanyId == 12 && s.IsOperational).OrderByDescending(s => s.CargoCapacity).Take(5).ToList();
```
**Code corrigé**
```csharp
var l = await db.Starships.AsNoTracking().Where(s => s.CompanyId == 12 && s.IsOperational).OrderByDescending(s => s.CargoCapacity).Take(5).ToListAsync();
```
**Détecter** : `ToQueryString()` sans `WHERE` ; « lignes lues » (2 M contre 5) dans `Mesurer` ; mémoire.

**Index** : `IX_Starships_CompanyId` (FK) sert le `WHERE` ; un index `(CompanyId, IsOperational, CargoCapacity DESC)` supprime le tri.

### 🧪 TP 12.1 — *Chasse au bug*
*Énoncé.* Trouvez, dans votre code, trois `ToList()` placés avant un `Where`, un `OrderBy` ou un `Count`. Pour chacun, mesurez avant/après.
<details><summary>Correction</summary>
Recherchez `ToList()` suivi de `.Where(`, `.OrderBy(`, `.Count(`, `.Select(` sur la ligne ou la suivante. Un *analyseur Roslyn* peut automatiser cette détection ; à défaut, la revue de code et la relecture de `ToQueryString()` suffisent.
</details>

---

## 12.2 Capture de variable et exécution différée

**Symptôme** : toutes les requêtes d'une liste retournent **le même résultat** ; ou la valeur utilisée n'est pas celle attendue. *Base : 0.2.*

**Code fautif**
```csharp
// Boucle for : « i » est UNE variable partagée, lue seulement à l'exécution
var requetes = new List<IQueryable<Good>>();
for (var i = 1; i <= 3; i++) requetes.Add(db.Goods.Where(g => g.Id == i));
foreach (var q in requetes) Console.WriteLine((await q.ToListAsync()).Single().Id);       // affiche 4, 4, 4 (valeur finale de i)
```
**Code corrigé**
```csharp
for (var i = 1; i <= 3; i++) { var id = i; requetes.Add(db.Goods.Where(g => g.Id == id)); }       // une variable par tour
// (dans un foreach, C# crée déjà une variable par itération)
```
**Détecter** : `ToQueryString()` affiche `@__i_0` — regardez sa **valeur** à l'exécution (journal avec `EnableSensitiveDataLogging`).

### 🧪 TP 12.2 — *Chasse au bug*
*Énoncé.* Expliquez pourquoi `foreach (var i in ids) requetes.Add(db.Goods.Where(g => g.Id == i))` fonctionne alors que la version `for` échoue.
<details><summary>Correction</summary>
Depuis C# 5, la variable d'itération d'un `foreach` est **recréée à chaque tour** ; celle d'un `for` ne l'est pas. La fermeture capture la **variable**, pas sa valeur à l'instant de la création.
</details>

---

## 12.3 Double énumération

**Symptôme** : une méthode envoie deux ou trois fois la même requête ; un résultat qui change entre deux lectures. *Base : 0.2, 6.4.*

**Code fautif**
```csharp
IEnumerable<Market> Marches(StarWarsContext db) => db.Markets.Where(m => m.IsBlackMarket);      // retourne une requête, pas une liste
var ms = Marches(db);
if (ms.Any()) Console.WriteLine($"{ms.Count()} marchés, premier : {ms.First().Name}");           // 3 requêtes, résultats potentiellement différents
```
**Code corrigé**
```csharp
var l = await db.Markets.Where(m => m.IsBlackMarket).Take(1_000).ToListAsync();
if (l.Count > 0) Console.WriteLine($"{l.Count} marchés, premier : {l[0].Name}");
```
**Détecter** : `CompteurSql` (3 contre 1) ; avertissements d'analyse « possible multiple enumeration ».

### 🧪 TP 12.3 — *Chasse au bug*
*Énoncé.* Écrivez un test qui échoue si une méthode du service émet plus de 2 requêtes pour une opération.
<details><summary>Correction</summary>
Le budget de requêtes (11.10). Il détecte aussi les N+1 (12.6).
</details>

---

## 12.4 `Count() > 0` et `Count()` sur grosse table

**Symptôme** : une validation « existe-t-il ? » ralentit avec la taille de la table. *Base : 1.3, 6.4, 9.6.*

**Code fautif**
```csharp
if (await db.Trades.CountAsync(t => t.CompanyId == 5 && t.Quantity > 400) > 0) { /* … */ }
```
**Code corrigé**
```csharp
if (await db.Trades.AnyAsync(t => t.CompanyId == 5 && t.Quantity > 400)) { /* … */ }
```
**Détecter** : `COUNT(*)` dans le SQL ; *logical reads* de `COUNT` contre `EXISTS`. **Nuance** : depuis EF 9, `collection.Count != 0` **dans** un `Where` devient `EXISTS` ; le terminal `CountAsync() > 0` reste un `COUNT(*)`.

**Index** : un index `(CompanyId, Quantity)` fait de l'`Any` un seek ; sans lui, il s'arrête dès la première ligne si elle est proche, sinon il scanne.

### 🧪 TP 12.4 — *Chasse au bug*
*Énoncé.* Mesurez `Count() > 0` et `Any()` quand il y a beaucoup de résultats, un seul résultat, aucun résultat. Dans quel cas l'écart est-il le plus grand ?
<details><summary>Correction</summary>
Beaucoup de résultats : `Any` s'arrête vite, `Count` lit tout (écart maximal). Aucun résultat : les deux parcourent tout sans index (écart nul) ; avec un index, les deux sont des seeks.
</details>

---

## 12.5 `Include` multiples et explosion cartésienne

**Symptôme** : une requête qui ramène des centaines de Mo pour quelques centaines d'entités. *Base : 3.7.*

**Code fautif**
```csharp
var l = await db.Companies.Where(c => c.Id <= 2_000).Include(c => c.Starships).Include(c => c.Trades).AsSingleQuery().ToListAsync();
```
**Code corrigé**
```csharp
var l = await db.Companies.Where(c => c.Id <= 2_000).Include(c => c.Starships).Include(c => c.Trades).AsSplitQuery().ToListAsync();
// ou une projection avec les seuls totaux nécessaires
```
**Détecter** : avertissement `MultipleCollectionIncludeWarning` (niveau `Query`) ; Mo reçus (`MesurerReseau`) ; lignes lues = **produit** des cardinalités.

### 🧪 TP 12.5 — *Chasse au bug*
*Énoncé.* Calculez à l'avance le nombre de lignes de la requête fautive à partir des moyennes (≈ 2 vaisseaux × ≈ 6 échanges par société), puis vérifiez.
<details><summary>Correction</summary>
≈ 12 lignes par société en *single query* contre ≈ 8 en *split* (somme) : multipliez par 2 000.
</details>

---

## 12.6 N+1 (lazy loading, `Find` en boucle)

**Symptôme** : des milliers de petites requêtes identiques ; la page est lente seulement avec beaucoup de données. *Base : 3.5, 3.10.*

**Code fautif**
```csharp
var societes = await db.Companies.Where(c => c.Id <= 1_000).ToListAsync();
var total = 0; foreach (var c in societes) total += (await db.Starships.CountAsync(s => s.CompanyId == c.Id));
```
**Code corrigé**
```csharp
var total = await db.Starships.CountAsync(s => s.CompanyId <= 1_000);
// ou, pour une valeur par société : un GroupBy en une requête
```
**Détecter** : `CompteurSql` ; dans `sys.dm_exec_query_stats`, **une** requête avec un `execution_count` énorme ; dans une trace, un « peigne » de spans identiques.

### 🧪 TP 12.6 — *Chasse au bug*
*Énoncé.* Trouvez le N+1 caché dans : `foreach (var p in planetes) Console.WriteLine(p.StarSystem.Name);` avec lazy loading ; et dans `planetes.Select(p => db.Markets.Count(m => m.PlanetId == p.Id)).ToList()`.
<details><summary>Correction</summary>
Le premier déclenche une requête de navigation par planète (lazy loading) ; le second une requête par élément dans le `Select` en mémoire. Corrections : `Include`/projection pour le premier ; `GroupBy` sur `Markets` pour le second.
</details>

---

## 12.7 `Contains` avec une liste énorme

**Symptôme** : une requête qui échoue sur « trop de paramètres », ou un plan différent à chaque taille de liste. *Base : 2.6, 7.4.6, 9.4.*

**Code fautif**
```csharp
var noms = await db.Species.AsNoTracking().Take(5_000).Select(s => s.Name).ToListAsync();
var l = await db.Goods.Where(g => noms.Contains(g.Name)).ToListAsync();                 // 5 000 valeurs sur une colonne NON indexée
```
**Code corrigé**
```csharp
foreach (var lot in noms.Chunk(500)) l.AddRange(await db.Goods.Where(g => lot.Contains(g.Name)).ToListAsync());
// ou un paramètre table (7.4.6), ou une jointure si la liste vient de la base
```
**Détecter** : taille du SQL ; plan (scan de `Goods` malgré le `IN`) ; nombre de plans en cache. **Index** : `IX_Lab_Goods_Name (Name)` transforme le scan en 5 000 seeks (ou en jointure).

### 🧪 TP 12.7 — *Chasse au bug*
*Énoncé.* Mesurez `Contains` avec 10, 100, 1 000, 5 000 valeurs sur `Name` avec et sans index.
<details><summary>Correction</summary>
Sans index, chaque taille scanne la table ; avec, le coût croît avec le nombre de valeurs (seeks). Le plan peut basculer d'un *Nested Loops* de seeks à un *Hash Join* à partir d'une certaine taille.
</details>

---

## 12.8 Fonction non traduisible dans un `Where`

**Symptôme** : `InvalidOperationException … could not be translated` ; ou, pire, un contournement par `AsEnumerable()` qui charge tout. *Base : 0.5, 2.2.*

**Code fautif**
```csharp
static bool Rare(Good g) => g.BasePrice > 90_000 && g.IsContraband;
var l = await db.Goods.Where(g => Rare(g)).ToListAsync();                                        // exception
var l2 = db.Goods.AsEnumerable().Where(Rare).ToList();                                           // « corrigé »… en chargeant 1 M de lignes
```
**Code corrigé**
```csharp
public static readonly Expression<Func<Good, bool>> RareExpr = g => g.BasePrice > 90_000 && g.IsContraband;
var l = await db.Goods.Where(RareExpr).ToListAsync();                                            // règle sous forme d'expression (voir 7.6.2 pour la réutiliser aussi en mémoire)
```
**Détecter** : l'exception ; le message cite l'expression ; le **contournement** se voit à l'absence de `WHERE`.

### 🧪 TP 12.8 — *Chasse au bug*
*Énoncé.* Un collègue a « résolu » l'erreur en ajoutant `.ToList()` avant le `Where`. Expliquez ce qu'il a fait et mesurez l'impact.
<details><summary>Correction</summary>
Il a remplacé une erreur **bruyante** par un défaut de performance **silencieux** : toute la table est rapatriée. À refuser en revue.
</details>

---

## 12.9 `Skip/Take` sans `OrderBy` déterministe

**Symptôme** : des lignes qui apparaissent sur deux pages ou ne sont jamais affichées. *Base : 1.2, 6.5.*

**Code fautif**
```csharp
var vus = new HashSet<int>(); var lus = 0;
for (var page = 0; page < 100; page++)
    foreach (var i in await db.Inhabitants.AsNoTracking().OrderBy(i => i.BirthYear).Skip(page * 50).Take(50).ToListAsync()) { lus++; vus.Add(i.Id); }
Console.WriteLine($"{lus} lues, {vus.Count} distinctes");                       // distinctes < lues : doublons entre pages
```
**Code corrigé**
```csharp
.OrderBy(i => i.BirthYear).ThenBy(i => i.Id)                                    // clé unique en dernier critère
```
**Détecter** : avertissement `RowLimitingOperationWithoutOrderByWarning` (sans tri) ; test comptant les identifiants distincts.

### 🧪 TP 12.9 — *Chasse au bug*
*Énoncé.* Prouvez que `OrderBy(BirthYear)` seul est non déterministe en créant un index et en relançant.
<details><summary>Correction</summary>
Le tri sur une colonne à peu de valeurs distinctes laisse l'ordre des ex æquo **au bon vouloir du plan** : un nouvel index peut changer l'ordre relatif des lignes et donc le contenu des pages.
</details>

---

## 12.10 `First` / `Single` / `Last` : comportements

**Symptôme** : un « premier » élément qui **change** après une modification d'index ; une exception `Single` en production. *Base : 1.3.*

**Code fautif**
```csharp
// Quel habitant est « le premier » de genre 1 ? Aucun tri : le moteur choisit
var a = await db.Inhabitants.Where(i => i.Gender == 1).FirstAsync();
// Créez un index (Gender, Credits DESC) puis relancez : le résultat peut changer
var b = await db.Goods.Where(g => g.IsContraband).SingleAsync();                // plusieurs lignes : exception InvalidOperationException
```
**Code corrigé**
```csharp
var a = await db.Inhabitants.Where(i => i.Gender == 1).OrderBy(i => i.Id).FirstAsync();           // ordre explicite
var b = await db.Goods.Where(g => g.Sku == sku).SingleOrDefaultAsync();                           // clé unique ; null si absent
```
**Détecter** : `TOP(1)` / `TOP(2)` sans `ORDER BY` dans le SQL ; test sur données où plusieurs lignes correspondent. `Last` **exige** un `OrderBy`.

### 🧪 TP 12.10 — *Chasse au bug*
*Énoncé.* Relevez l'`Id` retourné par `FirstAsync()` sans tri avant et après la création de `IX_Lab_Inhabitants_Gender_Credits` (voir 6.7). Qu'en déduisez-vous ?
<details><summary>Correction</summary>
Le premier élément retourné **dépend du plan** ; un changement d'index ou de statistiques change le résultat d'un code qui ne le spécifiait pas. Toujours ordonner.
</details>

---

## 12.11 Collation et `ToLower()` non sargable

**Symptôme** : une recherche insensible à la casse lente alors que la colonne est indexée. *Base : 0.7, 2.9.*

**Code fautif**
```csharp
await db.Companies.CountAsync(c => c.Name.ToLower() == "kamorooine logistics");          // LOWER([Name]) = … : scan
```
**Code corrigé**
```csharp
await db.Companies.CountAsync(c => c.Name == "kamorooine logistics");                    // collation CI_AS : insensible à la casse
```
**Détecter** : `LOWER(` dans le SQL ; *Index Scan* dans le plan. **Index** : sans index sur `Name`, les deux écritures scannent ; avec `IX_Lab_Companies_Name (Name)`, seule la seconde fait un seek.

```sql
CREATE INDEX IX_Lab_Companies_Name ON dbo.Companies (Name);
```

### 🧪 TP 12.11 — *Chasse au bug*
*Énoncé.* Retrouvez la collation de la base et d'une colonne ; montrez qu'une comparaison sensible à la casse exige `Collate`.
<details><summary>Correction</summary>
`SELECT DATABASEPROPERTYEX('StarWarsTrade','Collation')` ; `EF.Functions.Collate(c.Name, "Latin1_General_CS_AS")` : non sargable sur un index CI.
</details>

---

## 12.12 Agrégats sur ensemble vide

**Symptôme** : `InvalidOperationException: Sequence contains no elements` en production quand un filtre ne retourne rien. *Base : 0.7, 1.4.*

**Code fautif**
```csharp
var max = await db.Goods.Where(g => g.Id < 0).MaxAsync(g => g.BasePrice);                // lève sur ensemble vide
var moy = await db.Goods.Where(g => g.Id < 0).AverageAsync(g => g.BasePrice);            // idem
```
**Code corrigé**
```csharp
var max = await db.Goods.Where(g => g.Id < 0).MaxAsync(g => (decimal?)g.BasePrice);      // null si vide
var moy = (await db.Goods.Where(g => g.Id < 0).AverageAsync(g => (decimal?)g.BasePrice)) ?? 0m;
```
**Détecter** : test avec un jeu de données vide pour **chaque** agrégat ; comportement de `Sum` (0) différent de `Max`/`Average` (exception ou null selon le type).

### 🧪 TP 12.12 — *Chasse au bug*
*Énoncé.* Pour `Sum`, `Min`, `Max`, `Average`, `Count` sur un ensemble vide, dites ce que renvoie chaque opérateur en type non nullable et nullable.
<details><summary>Correction</summary>
`Count` : 0 ; `Sum` : 0 ; `Min`/`Max`/`Average` : exception en type non nullable, `null` en type nullable. À vérifier avec un test.
</details>

---

## 12.13 `DateTime.Now` : client ou serveur

**Symptôme** : des résultats décalés selon le fuseau, ou entre le serveur d'application et celui de la base. *Base : 0.7.*

**Code fautif**
```csharp
// DateTime.UtcNow DANS la requête : traduit en horloge SERVEUR (GETUTCDATE), pas en valeur évaluée côté client
var q = db.Trades.Where(t => t.TradedAt > DateTime.UtcNow.AddDays(-30));
Console.WriteLine(q.ToQueryString());                                                    // à vérifier : GETUTCDATE() dans le SQL ?
```
**Code corrigé**
```csharp
var depuis = DateTime.UtcNow.AddDays(-30);                                               // évalué UNE fois, côté application
var q = db.Trades.Where(t => t.TradedAt > depuis);                                        // un paramètre : valeur contrôlée, plan « sniffé »
```
**Détecter** : `ToQueryString()` — fonction de date dans le SQL ou paramètre. **Effets** : horloge différente entre serveurs ; **le plan** : une fonction de date ne se « renifle » pas (estimation générique), un paramètre oui (2.5).

### 🧪 TP 12.13 — *Chasse au bug*
*Énoncé.* Comparez le plan et les lectures de `TradedAt > DateTime.UtcNow.AddDays(-30)` et de `TradedAt > @depuis`, avec et sans index sur `TradedAt` (2.9). Concluez.
<details><summary>Correction</summary>
Même plan quand l'estimation est correcte ; l'écart apparaît avec des distributions asymétriques. L'argument décisif est la **maîtrise de la valeur** (tests déterministes avec une horloge injectée).
</details>

---

## 12.14 `DbContext` partagé entre threads

**Symptôme** : `InvalidOperationException : A second operation was started on this context instance before a previous operation completed`. *Base : 8.12.*

**Code fautif**
```csharp
// Dans un service ASP.NET : un contexte « scoped » partagé par plusieurs tâches parallèles
await Task.WhenAll(db.Goods.CountAsync(), db.Markets.CountAsync(), db.Planets.CountAsync());
```
**Code corrigé**
```csharp
// Séquentiel (le plus simple) :
var a = await db.Goods.CountAsync(); var b = await db.Markets.CountAsync(); var c = await db.Planets.CountAsync();
// …ou un contexte PAR tâche avec IDbContextFactory<StarWarsContext> (8.12)
```
**Détecter** : l'exception (intermittente : elle dépend du chevauchement) ; revue des `Task.WhenAll`/`Parallel` qui capturent un contexte.

### 🧪 TP 12.14 — *Chasse au bug*
*Énoncé.* Reproduisez l'exception avec trois `CountAsync` en `Task.WhenAll` sur un contexte partagé, puis corrigez avec la fabrique.
<details><summary>Correction</summary>
Un `DbContext` n'est **pas thread-safe** : une opération à la fois. Chaque tâche prend `await using var c = await fabrique.CreateDbContextAsync();`.
</details>

---

## 12.15 Tracking massif oublié

**Symptôme** : `OutOfMemoryException` ou GC en génération 2 permanent lors d'un traitement. *Base : 4.1, 4.3.*

**Code fautif**
```csharp
await foreach (var i in db.Inhabitants.AsAsyncEnumerable()) { /* traitement */ }          // 4 M d'entités SUIVIES au fil de la lecture
```
**Code corrigé**
```csharp
await foreach (var i in db.Inhabitants.AsNoTracking().Select(i => new { i.Id, i.Credits }).AsAsyncEnumerable()) { /* traitement */ }
```
**Détecter** : `dotnet-counters` (`GC Heap Size` qui monte sans plafond) ; `ChangeTracker.Entries().Count()`.

### 🧪 TP 12.15 — *Chasse au bug*
*Énoncé.* Mesurez le pic mémoire d'un parcours de 1 M d'habitants avec et sans `AsNoTracking`. Quelle est la proportion ?
<details><summary>Correction</summary>
Voir 4.1 et 8.11 : le coût par entité suivie est un multiple de la taille de l'objet. Mesurez l'octet/entité sur **votre** table.
</details>

---

## 12.16 `.Result` / `.Wait()` sur du code async

**Symptôme** : l'application ralentit **par paliers** sous charge, la file d'attente des threads grossit, sans CPU élevé. *Aucun chapitre précédent.*

**Code fautif**
```csharp
int CompterBloquant() => NewDb().Goods.CountAsync().Result;                              // bloque un thread du pool pendant l'attente SQL
```
**Code corrigé**
```csharp
async Task<int> CompterAsync() { await using var d = NewDb(); return await d.Goods.CountAsync(); }
```
**Mettre en évidence** (avec peu de threads disponibles, comme sous charge) :

```csharp
ThreadPool.SetMinThreads(4, 4);                                                             // simule un pool contraint
var sw = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => CompterBloquant())));
Console.WriteLine($"bloquant : {sw.ElapsedMilliseconds} ms");
sw.Restart();
await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => CompterAsync()));
Console.WriteLine($"asynchrone : {sw.ElapsedMilliseconds} ms");
```
**À observer** : dans la version bloquante, le pool **injecte** les threads lentement (famine de threads) : temps beaucoup plus long ; `dotnet-counters` : `ThreadPool Queue Length`, `ThreadPool Thread Count`. Dans une application avec **contexte de synchronisation** (anciens ASP.NET, interfaces graphiques), `.Result` peut en plus provoquer un **interblocage**.

### 🧪 TP 12.16 — *Chasse au bug*
*Énoncé.* Faites varier le nombre de requêtes concurrentes (10, 100, 1 000) pour les deux versions et tracez le temps. À partir de quand la version bloquante s'effondre-t-elle ?
<details><summary>Correction</summary>
Quand le nombre d'appels bloquants dépasse le nombre minimum de threads du pool, chaque thread supplémentaire demande du temps d'injection : la courbe devient en escalier. La version asynchrone reste linéaire.
</details>

---

## 12.17 `IEnumerable<T>` en paramètre de repository

**Symptôme** : une méthode du dépôt « marche » en test (100 lignes) et charge toute la table en production. *Base : 0.1, 0.6.*

**Code fautif**
```csharp
public interface IVaisseaux { IEnumerable<Starship> Tous(); }
public class DepotVaisseaux(StarWarsContext db) : IVaisseaux { public IEnumerable<Starship> Tous() => db.Starships; }
// Appelant : repo.Tous().Where(s => s.IsOperational).Take(10).ToList()   → Enumerable.Where : 2 M de lignes
```
**Code corrigé**
```csharp
public interface IVaisseaux { IQueryable<Starship> Tous(); }                             // reste composable (attention à la durée de vie du contexte)
// ou des méthodes qui prennent les critères : Task<List<Starship>> Operationnels(int max)
```
**Détecter** : revue des signatures (`IEnumerable`/`List` retournés par un dépôt) ; `ToQueryString()` sans `WHERE` ; **un test avec un grand volume**.

**Contrepartie** : exposer un `IQueryable` permet à l'appelant d'écrire n'importe quelle requête (et de l'**exécuter après la libération du contexte**) : beaucoup d'équipes préfèrent des **méthodes précises** retournant des `List<T>` bornées.

### 🧪 TP 12.17 — *Chasse au bug*
*Énoncé.* Concevez l'interface du dépôt pour qu'elle **interdise** les lectures non bornées.
<details><summary>Correction</summary>
Des méthodes qui exigent une **taille de page** (`Page(int taille, int apres)`), retournent des **projections** (DTO) et n'exposent pas `IQueryable`.
</details>

---

## 12.18 `Func` au lieu d'`Expression`

**Symptôme** : le même que 12.17, avec un code qui paraît correct. *Base : 0.1, 7.2.3.*

**Code fautif**
```csharp
public List<Trade> Chercher(Func<Trade, bool> critere) => db.Trades.AsNoTracking().Where(critere).Take(20).ToList();
```
**Code corrigé**
```csharp
public Task<List<Trade>> Chercher(Expression<Func<Trade, bool>> critere) => db.Trades.AsNoTracking().Where(critere).OrderBy(t => t.Id).Take(20).ToListAsync();
```
**Détecter** : l'analyseur (règle personnalisée interdisant `Func<` dans les signatures de dépôt) ; `ToQueryString()` ; un test qui vérifie `WHERE`.

### 🧪 TP 12.18 — *Chasse au bug*
*Énoncé.* Écrivez un test réflexion qui parcourt les méthodes publiques d'un dépôt et échoue si l'une accepte un `Func<T,bool>`.
<details><summary>Correction</summary>
`typeof(Depot).GetMethods().Any(m => m.GetParameters().Any(p => p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)))` — le test garde la règle sans dépendre d'une relecture humaine.
</details>

---

## 12.19 Filtres globaux oubliés ou non indexés

**Symptôme** : des totaux qui ne correspondent pas entre l'écran et le rapport d'administration ; ou toutes les requêtes d'une entité ralentissent après l'ajout d'un filtre. *Base : 3.12, 6.14, 9.8.*

**Code fautif**
```csharp
// Contexte avec filtre global !m.IsBlackMarket
var visible = await db.Markets.CountAsync();                                              // sans les marchés noirs
var rapport = await db.Markets.CountAsync();                                              // le rapport d'audit devait les inclure !
```
**Code corrigé**
```csharp
var rapport = await db.Markets.IgnoreQueryFilters().CountAsync();                         // intention explicite
```
**Détecter** : comparer `ToQueryString()` avec et sans `IgnoreQueryFilters()` ; un test qui compte les lignes avec et sans filtre ; le plan : jointure ajoutée si le filtre passe par une navigation.

**Index** : un filtre sur colonne non indexée est un prédicat ajouté à tous les scans ; un index filtré aligné (3.12) le rend gratuit.

### 🧪 TP 12.19 — *Chasse au bug*
*Énoncé.* Inventoriez les entités avec filtre global et, pour chacune, les requêtes d'administration/rapports : lesquelles devraient utiliser `IgnoreQueryFilters()` ?
<details><summary>Correction</summary>

Recherche : `HasQueryFilter(` pour l'inventaire, puis revue des requêtes sans contexte utilisateur (jobs, rapports). Les filtres **nommés** (EF 10) permettent de ne lever que celui qui doit l'être (9.8).
</details>

---

## 12.20 Constantes variables dans les requêtes

**Symptôme** : le cache de requêtes EF ou le cache de plans SQL grossit sans fin ; CPU de compilation élevé. *Base : 0.4, 2.5, 7.1.1.*

**Code fautif**
```csharp
// Expression construite avec Expression.Constant, ou EF.Constant(valeur), ou SQL concaténé
for (var i = 1; i <= 2_000; i++) await db.Goods.Where(g => g.Id == EF.Constant(i)).FirstAsync();
```
**Code corrigé**
```csharp
for (var i = 1; i <= 2_000; i++) { var id = i; await db.Goods.Where(g => g.Id == id).FirstAsync(); }
```
**Détecter** : `dotnet counters monitor --counters Microsoft.EntityFrameworkCore` : le **taux de succès du cache de requêtes** s'effondre (11.4) ; nombre de plans SQL en cache (`sys.dm_exec_cached_plans`) ; mémoire du cache de plans.

### 🧪 TP 12.20 — *Chasse au bug*
*Énoncé.* Cherchez dans un code existant : `EF.Constant(`, `Expression.Constant(`, `FromSqlRaw("…" + `, interpolation dans `ExecuteSqlRaw`. Lesquelles produisent un SQL différent par valeur ?
<details><summary>Correction</summary>
Toutes. Les alternatives sûres sont `FromSql`/`ExecuteSql` avec **interpolation** (paramètres) et les variables capturées.
</details>

---

## Grille de détection : du symptôme à l'outil

| Symptôme observé | Piège(s) probable(s) | Premier outil |
|---|---|---|
| mémoire qui explose | 12.1, 12.5, 12.15, 12.17, 12.18 | `dotnet-counters`, `ToQueryString` |
| des milliers de petites requêtes | 12.3, 12.6 | `CompteurSql`, traces |
| une requête qui ramène trop de lignes | 12.1, 12.5, 12.17, 12.18 | `ToQueryString` (pas de `WHERE`) |
| lenteur qui croît avec la table | 12.4, 12.11, 12.7 | `STATISTICS IO`, plan |
| résultats incohérents | 12.2, 12.9, 12.10, 12.13, 12.19 | tests, `ToQueryString`, journal |
| exception intermittente | 12.14, 12.16 | revue, `dotnet-counters` |
| exception à données vides | 12.12 | tests avec jeu vide |
| caches qui gonflent | 12.7, 12.20 | compteurs EF, DMV de plans |

## Check-list de revue de code

- [ ] Aucun `ToList()`/`AsEnumerable()` avant `Where`/`OrderBy`/`Count`/`Take`.
- [ ] Aucun `Func<>` ni `IEnumerable<T>` dans les signatures d'accès aux données.
- [ ] Tout `Take`/`Skip` précédé d'un `OrderBy` **déterministe**.
- [ ] Existence = `Any()`, pas `Count() > 0`.
- [ ] Pas de requête dans une boucle ; pas de navigation lazy dans une boucle.
- [ ] Plus d'un `Include` de collection ⇒ `AsSplitQuery()` ou projection.
- [ ] Agrégats testés sur ensemble vide.
- [ ] Valeurs capturées, pas de constantes variables ; dates évaluées côté application.
- [ ] Lectures en `AsNoTracking()` (ou défaut global) ; traitement en lots ⇒ `Clear()`.
- [ ] Aucun `.Result`/`.Wait()` ; jeton d'annulation propagé.
- [ ] Un `DbContext` par tâche concurrente.
- [ ] Filtres globaux inventoriés, `IgnoreQueryFilters()` explicite quand nécessaire.

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Goods_Name              ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Companies_Name          ON dbo.Companies;
DROP INDEX IF EXISTS IX_Lab_Inhabitants_Gender_Credits ON dbo.Inhabitants;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
```
