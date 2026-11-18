# 07 — Performance : techniques avancées

> Suite de [`06-techniques-simples.md`](06-techniques-simples.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans, `ToQueryString`), `MesurerReseau` du [03](03-donnees-liees.md), `DansTransactionAnnulee` du [05](05-ecritures.md).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**. Les fonctionnalités dépendant de la **version d'EF Core / de SQL Server** sont marquées *(à vérifier)*.
> Index de démonstration : `IX_Lab_` ; objets SQL : `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`. Les classes et méthodes d'aide se déclarent **en bas de `Program.cs`**, après les instructions.
> Les notions déjà démontrées plus haut (compilation, pool, keyset, sniffing, streaming) sont reprises ici **avec un angle nouveau** ; le renvoi indique où lire la base.

## Sommaire

**7.1 Compilation et caches** — [7.1.1 cache de requêtes](#711-cache-de-requêtes-et-constantes) · [7.1.2 requêtes compilées](#712-requêtes-compilées) · [7.1.3 modèle compilé](#713-modèle-compilé) · [7.1.4 requêtes précompilées](#714-requêtes-précompilées-nativeaot) · [7.1.5 pool de contextes](#715-pool-de-contextes)
**7.2 Composition dynamique** — [7.2.1 prédicats dynamiques](#721-prédicats-dynamiques) · [7.2.2 `IQueryable` conditionnel](#722-iqueryable-conditionnel-et-ordre-des-opérateurs) · [7.2.3 spécifications](#723-le-pattern-spécification) · [7.2.4 tris dynamiques](#724-tris-dynamiques) · [7.2.5 `Expression.Invoke`](#725-éviter-expressioninvoke)
**7.3 Pagination avancée** — [7.3.1 keyset](#731-pagination-par-clé-keyset) · [7.3.2 sans `COUNT`](#732-pagination-sans-count) · [7.3.3 comptage approché](#733-comptage-approché)
**7.4 Requêtes ensemblistes** — [7.4.1 NOT EXISTS / LEFT JOIN / NOT IN](#741-not-exists-left-join--is-null-not-in) · [7.4.2 top N par groupe](#742-top-n-par-groupe) · [7.4.3 sous-requêtes corrélées](#743-sous-requêtes-corrélées-et-jointures) · [7.4.4 agrégats corrélés multiples](#744-agrégats-corrélés-multiples) · [7.4.5 `Distinct` → `EXISTS`](#745-distinct--exists) · [7.4.6 `Contains` volumineux](#746-contains-volumineux)
**7.5 Contrôle de l'exécution SQL** — [7.5.1 intercepteur et hints](#751-intercepteur-de-commandes-et-hints) · [7.5.2 *parameter sniffing*](#752-parameter-sniffing) · [7.5.3 timeouts et annulation](#753-timeouts-et-annulation)
**7.6 Arbres d'expression** — [7.6.1 construire](#761-construire-une-expression) · [7.6.2 réécrire](#762-réécrire-un-arbre-expressionvisitor) · [7.6.3 `IQueryExpressionInterceptor`](#763-iqueryexpressioninterceptor)
**7.7 Matérialisation et mémoire** — [7.7.1 flux](#771-streaming-et-traitement-par-lots) · [7.7.2 colonnes volumineuses](#772-colonnes-volumineuses-max--binaire) · [7.7.3 LOH](#773-le-tas-des-grands-objets-loh)
[Script de nettoyage](#script-de-nettoyage)

---

# 7.1 Compilation et caches

## 7.1.1 Cache de requêtes et constantes

**Axe concerné : ⚙️ .NET (cache de requêtes EF) + 🗄️ SQL (cache de plans).**

### La notion

EF Core met en cache les requêtes **par forme d'arbre**. Une **variable capturée** est extraite en paramètre : la forme reste la même. Une **constante écrite dans l'arbre** (`Expression.Constant(valeur)`, `EF.Constant`, valeur écrite en dur) fait de **chaque valeur une forme différente** : une compilation par valeur, une entrée de cache EF par valeur et un texte SQL différent par valeur (voir 0.4, 2.5). Le cas qui piège les experts : les **constructeurs d'expressions** dynamiques qui utilisent `Expression.Constant`.

### Dans quels cas on est concerné

Filtres générés dynamiquement à partir de listes de critères (écrans de recherche, API de filtrage générique).

### Exemple sans l'amélioration

```csharp
// ❌ Constructeur de filtre avec Expression.Constant : une forme par valeur
static Expression<Func<Trade, bool>> EgalBad(int valeur)
{
    var x = Expression.Parameter(typeof(Trade), "x");
    var corps = Expression.Equal(Expression.Property(x, nameof(Trade.GoodId)), Expression.Constant(valeur));
    return Expression.Lambda<Func<Trade, bool>>(corps, x);
}
await Mesurer("AVANT : Expression.Constant ×300", async db =>
{ var n = 0; for (var i = 1; i <= 300; i++) n += await db.Trades.CountAsync(EgalBad(i)); return n; });
```

### Exemple avec l'amélioration

```csharp
// ✔ La valeur est capturée par une fermeture : EF la voit comme un paramètre
static Expression<Func<Trade, bool>> EgalOk(int valeur)
{
    Expression<Func<int>> capt = () => valeur;                         // MemberExpression sur la fermeture
    var x = Expression.Parameter(typeof(Trade), "x");
    return Expression.Lambda<Func<Trade, bool>>(Expression.Equal(Expression.Property(x, nameof(Trade.GoodId)), capt.Body), x);
}
await Mesurer("APRÈS : valeur capturée ×300", async db =>
{ var n = 0; for (var i = 1; i <= 300; i++) n += await db.Trades.CountAsync(EgalOk(i)); return n; });
```
**À observer** : `ToQueryString()` des deux (`= 17` en dur contre `= @__valeur_0`) ; le nombre de plans dans le cache SQL (`DBCC FREEPROCCACHE` avant, comptage après ; voir 0.4) ; le temps C# par appel.

### Sans index / avec index

`Trades.GoodId` est indexé (`IX_Trades_GoodId`) : chaque requête est un seek ; la pollution du cache coûte donc **seulement** de la compilation, pas de lecture.

### Comparaison avec une procédure stockée

Une procédure a un plan et un cache **par objet**, quelle que soit la valeur : elle ne souffre pas de ce piège (mais on en subit d'autres : sniffing, 7.5.2).

### 🧪 TP 7.1.1 — « Un filtre générique propre »

*Énoncé.* Écrivez `Egal<T,TV>(Expression<Func<T,TV>> sel, TV valeur)` qui retourne un prédicat d'égalité **paramétrable** pour n'importe quelle propriété. Prouvez avec `ToQueryString()` et le comptage de plans que 300 valeurs produisent 1 plan.

<details><summary>Correction</summary>

```csharp
static Expression<Func<T, bool>> Egal<T, TV>(Expression<Func<T, TV>> sel, TV valeur)
{
    Expression<Func<TV>> capt = () => valeur;
    return Expression.Lambda<Func<T, bool>>(Expression.Equal(sel.Body, capt.Body), sel.Parameters);
}
// usage : db.Trades.Where(Egal<Trade, int>(t => t.GoodId, 42))
```
Le piège de l'écriture « naturelle » (`Expression.Constant(valeur)`) est invisible dans le code, visible dans le SQL.
</details>

---

## 7.1.2 Requêtes compilées

**Axe concerné : ⚙️ .NET (CPU, allocations).** *Base : 0.4.*

### La notion

`EF.CompileQuery` / `EF.CompileAsyncQuery` précalculent le travail de traduction : à l'appel, seul reste l'envoi du SQL et la matérialisation. Une requête compilée retourne **soit un résultat scalaire/entité**, **soit un `IEnumerable`/`IAsyncEnumerable`** ; elle **ne se compose plus** ensuite (pas de `.Where` supplémentaire). Gain : quelques dizaines de µs et quelques Ko par appel.

### Dans quels cas on est concerné

*Hot paths* : milliers d'appels par seconde d'une même requête paramétrée. Pas pour des requêtes dynamiques (forme variable).

### Exemple sans l'amélioration

```csharp
// ❌ Liste des 20 derniers échanges d'une société, traduite à chaque appel (2 000 appels)
await Mesurer("AVANT : requête normale", async db =>
{
    var n = 0;
    for (var i = 1; i <= 2_000; i++)
        n += (await db.Trades.AsNoTracking().Where(t => t.CompanyId == i).OrderByDescending(t => t.TradedAt).Take(20).ToListAsync()).Count;
    return n;
});
```

### Exemple avec l'amélioration

```csharp
public static class Compilees
{
    public static readonly Func<StarWarsContext, int, IAsyncEnumerable<Trade>> DerniersEchanges =
        EF.CompileAsyncQuery((StarWarsContext c, int societe) =>
            c.Trades.AsNoTracking().Where(t => t.CompanyId == societe).OrderByDescending(t => t.TradedAt).Take(20));
}
await Mesurer("APRÈS : compilée", async db =>
{
    var n = 0;
    for (var i = 1; i <= 2_000; i++) await foreach (var _ in Compilees.DerniersEchanges(db, i)) n++;
    return n;
});
```
**À observer** : temps et allocations ; le **SQL identique** (`afficherSql` sur un seul appel) ; l'écart se **réduit** quand chaque requête coûte plus que le pipeline (ici, un tri par société).

### Sans index / avec index

Le tri par `TradedAt` après filtre par société : sans index `(CompanyId, TradedAt DESC)`, un `Sort` par société (6 échanges, négligeable). Pour des entités à plusieurs milliers d'enfants, l'index `(CompanyId, TradedAt DESC)` supprime le tri (voir 3.6).

### Comparaison avec une procédure stockée

Une procédure paramétrée (`Lab_DerniersEchanges @Societe`) a **le même coût SQL** ; elle économise le coût de **traduction** que la requête compilée supprime aussi. Mesurez les trois (normale, compilée, procédure) : l'écart entre compilée et procédure doit être faible.

### 🧪 TP 7.1.2 — « Quand compiler ? »

*Énoncé.* Mesurez le temps par appel de la requête sur une société **sans** échange (résultat vide, SQL trivial), puis sur une société à plusieurs milliers d'échanges (créez-en une en base de test ou choisissez la plus chargée). À partir de quand la compilation n'apporte-t-elle plus rien de mesurable ?

<details><summary>Correction</summary>

Quand le temps SQL + matérialisation dépasse largement le pipeline (quelques dizaines de µs), le gain devient invisible. La compilation est un outil pour les **requêtes très fréquentes et très légères**.
</details>

---

## 7.1.3 Modèle compilé

**Axe concerné : ⚙️ .NET (démarrage).**

### La notion

Au **premier** usage d'un `DbContext` dans un processus, EF construit son modèle (conventions, relations, 15 entités). Le **modèle compilé** (`dotnet ef dbcontext optimize`) génère le code C# du modèle ; le démarrage est plus rapide. Surtout visible pour de **gros modèles** et dans les processus de courte durée (fonctions serverless, outils CLI). Limites : certaines fonctionnalités ne sont pas prises en charge (filtres globaux, proxies de chargement différé… *à vérifier selon la version*).

### Dans quels cas on est concerné

Applications dont le **démarrage** compte : conteneurs qui redémarrent souvent, jobs courts.

### Exemple sans l'amélioration

```csharp
// Mesurer la PREMIÈRE requête d'un processus (à lancer dans un programme neuf, plusieurs fois)
var t0 = Stopwatch.GetTimestamp();
await using (var d = NewDb()) await d.Sectors.CountAsync();
Console.WriteLine($"1ʳᵉ requête : {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:N0} ms");
var t1 = Stopwatch.GetTimestamp();
await using (var d = NewDb()) await d.Sectors.CountAsync();
Console.WriteLine($"2ᵉ requête  : {Stopwatch.GetElapsedTime(t1).TotalMilliseconds:N0} ms");
```

### Exemple avec l'amélioration

```bash
dotnet ef dbcontext optimize --output-dir CompiledModels --namespace MasteringLinq.CompiledModels
```
```csharp
// dans NewDb : .UseModel(MasteringLinq.CompiledModels.StarWarsContextModel.Instance)
```
Relancez **le même programme** plusieurs fois (chaque lancement = un processus neuf) et comparez la 1ʳᵉ requête.

**À observer** : l'écart de la première requête ; aucun écart sur la deuxième ; le code généré (`CompiledModels/`) à **régénérer** après chaque changement de modèle.

### Sans index / avec index et procédure stockée

Sans objet : c'est le démarrage .NET.

### 🧪 TP 7.1.3 — « Le premier appel »

*Énoncé.* Mesurez 10 démarrages avec et sans modèle compilé (moyenne/médiane de la première requête). Quel gain attendez-vous pour un job qui tourne 2 secondes ? Pour une API qui tourne en continu ?

<details><summary>Correction</summary>

Pour un job court, le gain se compare au temps total (visible) ; pour une API en continu, il disparaît après le démarrage. Le modèle compilé est un **investissement de démarrage**, pas de régime permanent.
</details>

---

## 7.1.4 Requêtes précompilées (NativeAOT)

**Axe concerné : ⚙️ .NET (démarrage, AOT) — *(fonctionnalité évolutive : à vérifier dans votre version)*.**

### La notion

EF Core a introduit un mécanisme de **précompilation des requêtes** (`dotnet ef dbcontext optimize --precompile-queries`) qui remplace la traduction à l'exécution par du code généré à la compilation, prérequis du **NativeAOT**. Le statut (expérimental, limitations sur les requêtes dynamiques et certains opérateurs) évolue d'une version à l'autre : **vérifiez la documentation de votre version avant de présenter cette notion**.

### Dans quels cas on est concerné

Applications compilées en NativeAOT, démarrage ultra-rapide, taille de binaire réduite.

### Exemple (démarche à vérifier)

```bash
# ❌ sans précompilation : traduction à l'exécution (impossible en NativeAOT)
# ✔ avec : génération des requêtes
dotnet ef dbcontext optimize --precompile-queries --nativeaot
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```
**À observer** : taille et démarrage du binaire ; les **requêtes refusées** par le générateur (dynamiques) ; les avertissements.

### Sans index / avec index, procédure stockée

Sans objet.

### 🧪 TP 7.1.4 — « Ce que la précompilation refuse »

*Énoncé.* Essayez de précompiler : une requête fixe, une requête avec `Expression` dynamique (7.2.1), une requête avec `Include` conditionnel. Listez celles que le générateur accepte.

<details><summary>Correction</summary>

Attendu : les requêtes **statiques** passent ; les requêtes dont la **forme** dépend de données d'exécution ne le peuvent pas (la forme doit être connue à la compilation). C'est la contrepartie directe de la composition dynamique (7.2).
</details>

---

## 7.1.5 Pool de contextes

**Axe concerné : ⚙️ .NET (allocations) — et **fuite d'état**.** *Base : 0.4.*

### La notion

`AddDbContextPool` / `AddPooledDbContextFactory` recyclent les instances de `DbContext`. EF **réinitialise** son état interne (change tracker, etc.) à la restitution, mais **pas vos propres champs** : un `DbContext` du pool qui porte un état applicatif (identifiant de locataire, indicateur) peut le **conserver** pour l'emprunteur suivant.

### Dans quels cas on est concerné

Contexte avec propriétés personnalisées (locataire courant, utilisateur), services injectés mutables.

### Exemple sans l'amélioration

```csharp
// Dans votre classe partielle : public string? Locataire { get; set; }
using var sp = new ServiceCollection().AddPooledDbContextFactory<StarWarsContext>(o => o.UseSqlServer(Cs), poolSize: 4).BuildServiceProvider();
var f = sp.GetRequiredService<IDbContextFactory<StarWarsContext>>();

await using (var a = await f.CreateDbContextAsync()) a.Locataire = "ACME";            // 1er emprunt
await using (var b = await f.CreateDbContextAsync()) Console.WriteLine($"2ᵉ emprunt : Locataire = {b.Locataire ?? "(null)"}");   // ⚠ « ACME » peut survivre
```

### Exemple avec l'amélioration

```csharp
// ✔ Ne jamais stocker d'état applicatif dans un contexte poolé : le passer par requête…
static IQueryable<Planet> PourLocataire(StarWarsContext db, string locataire) => db.Planets.Where(p => /* filtre dépendant de locataire */ true);
// ✔ …ou implémenter IResettableService pour remettre l'état à zéro à la restitution (API avancée)
```
**À observer** : si la valeur survit (cela dépend de la taille du pool et de l'ordre) ; le gain d'allocations du pool (voir 0.4).

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 7.1.5 — « La fuite de locataire »

*Énoncé.* Reproduisez la fuite, puis corrigez-la sans désactiver le pool. Expliquez pourquoi un défaut dans un test unitaire (un seul contexte) ne l'aurait pas révélé.

<details><summary>Correction</summary>

Un test avec un seul emprunt ne réutilise jamais une instance déjà utilisée : la fuite n'apparaît que sous charge. Corrections : état passé en **paramètre de la requête**, ou nettoyé dans un `IResettableService`, ou contexte non poolé pour la partie qui porte un état.
</details>

---

# 7.2 Composition dynamique

## 7.2.1 Prédicats dynamiques

**Axe concerné : 🗄️ SQL (filtre exprimé dans le `WHERE`) + ⚙️ .NET.**

### La notion

Pour un écran de recherche dont les critères sont **choisis à l'exécution**, on construit un `Expression<Func<T,bool>>` en **combinant** des prédicats avec `And`/`Or`. Les lambdas ayant **chacune leur paramètre**, on ne peut pas simplement assembler leurs corps : il faut **remplacer** le paramètre (`ExpressionVisitor`). Alternative sans visiteur : chaîner des `Where` (équivaut à `AND`).

### Dans quels cas on est concerné

Recherche multicritères, filtres utilisateur, API de filtrage générique, `OR` dynamique (que `.Where().Where()` ne permet pas).

### Exemple sans l'amélioration

```csharp
// ❌ Filtre construit avec Func (évalué en mémoire) pour pouvoir le composer facilement
Func<Trade, bool> pred = t => t.GoodId < 100 && t.CompanyId < 100;       // composable (&&) mais exécuté côté client
await Mesurer("AVANT : Func combinés", db => Task.FromResult(db.Trades.AsNoTracking().Where(pred).Count()));      // 6 M de lignes rapatriées
```

### Exemple avec l'amélioration

```csharp
// Bas de Program.cs
public static class Predicats
{
    public static Expression<Func<T, bool>> Et<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Combiner(a, b, Expression.AndAlso);
    public static Expression<Func<T, bool>> Ou<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Combiner(a, b, Expression.OrElse);
    private static Expression<Func<T, bool>> Combiner<T>(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b, Func<Expression, Expression, BinaryExpression> op)
    {
        var p = a.Parameters[0];
        var corpsB = new Remplaceur(b.Parameters[0], p).Visit(b.Body)!;
        return Expression.Lambda<Func<T, bool>>(op(a.Body, corpsB), p);
    }
    private sealed class Remplaceur(ParameterExpression de, Expression vers) : ExpressionVisitor
    { protected override Expression VisitParameter(ParameterExpression n) => n == de ? vers : n; }
}
```
```csharp
Expression<Func<Trade, bool>> filtre = t => t.GoodId < 100;
filtre = filtre.Et(t => t.CompanyId < 100).Ou(t => t.Quantity == 500 && t.GoodId == 5);          // OR dynamique
await Mesurer("APRÈS : Expression combinée", db => db.Trades.CountAsync(filtre), afficherSql: true);
```
**À observer** : le SQL (`WHERE ([GoodId] < 100 AND [CompanyId] < 100) OR ([Quantity] = 500 AND [GoodId] = 5)`) ; le plan : avec un `OR` sur des colonnes différentes, SQL Server peut utiliser **deux seeks fusionnés** (`Concatenation`/`Merge`) ou un scan.

### Sans index / avec index

```sql
-- les deux branches du OR sont indexées (GoodId, CompanyId : FK) : comparez avec une version où l'une des branches porte sur une colonne non indexée (Quantity) → scan
CREATE INDEX IX_Lab_Trades_Qty_Good ON dbo.Trades (Quantity, GoodId);
-- relancer : la 2ᵉ branche devient un seek
DROP INDEX IX_Lab_Trades_Qty_Good ON dbo.Trades;
```
**Leçon** : un `OR` n'est efficace que si **chaque branche** est sargable et indexée.

### Comparaison avec une procédure stockée

Une procédure à critères optionnels (`WHERE (@a IS NULL OR col = @a) …`) souffre du sniffing et de plans sous-optimaux ; la solution classique est le **SQL dynamique** (`sp_executesql`) — c'est ce que la composition LINQ fait naturellement et de façon paramétrée.

### 🧪 TP 7.2.1 — « Recherche à la carte »

*Énoncé.* Écrivez `Construire(Dictionary<string, object?> criteres)` qui retourne un prédicat sur `Trade` combinant en `AND` les critères égalité fournis (`GoodId`, `CompanyId`, `TradeType`) et un critère `Quantity` minimal optionnel. Vérifiez `ToQueryString()` pour 3 combinaisons et le nombre de plans en cache.

<details><summary>Correction</summary>

```csharp
static Expression<Func<Trade, bool>> Construire(Dictionary<string, object?> c)
{
    Expression<Func<Trade, bool>> p = t => true;
    if (c.TryGetValue("GoodId", out var g))       p = p.Et(Egal<Trade, int>(t => t.GoodId, (int)g!));
    if (c.TryGetValue("CompanyId", out var co))   p = p.Et(Egal<Trade, int>(t => t.CompanyId, (int)co!));
    if (c.TryGetValue("TradeType", out var tt))   p = p.Et(Egal<Trade, int>(t => t.TradeType, (int)tt!));
    if (c.TryGetValue("QuantiteMin", out var q))  { var min = (int)q!; p = p.Et(t => t.Quantity >= min); }
    return p;
}
```
Chaque combinaison de critères est **une forme** (donc un plan) : trois combinaisons = trois plans, indépendamment des valeurs. (Reprend `Egal` du TP 7.1.1.)
</details>

---

## 7.2.2 `IQueryable` conditionnel et ordre des opérateurs

**Axe concerné : 🗄️ SQL + justesse.**

### La notion

On construit la requête par `if (critère) q = q.Where(…)`. Le piège n'est pas la vitesse mais l'**ordre** : un `Take`/`Skip` placé **avant** un `Where` change la **sémantique** (le filtre porte sur les N premières lignes), et un `OrderBy` placé après `Take` n'a pas l'effet voulu. Une requête mal composée **fonctionne** et renvoie un résultat **faux**.

### Dans quels cas on est concerné

Pagination + filtres appliqués dans des couches différentes.

### Exemple sans l'amélioration

```csharp
// ❌ Le filtre est appliqué APRÈS Take(100) : on filtre les 100 premiers, pas toute la table
await Mesurer("AVANT : Take puis Where", async db =>
    (await db.Starships.AsNoTracking().OrderBy(s => s.Id).Take(100).Where(s => s.IsOperational).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Le filtre, puis le tri, puis la borne
await Mesurer("APRÈS : Where puis Take", async db =>
    (await db.Starships.AsNoTracking().Where(s => s.IsOperational).OrderBy(s => s.Id).Take(100).ToListAsync()).Count, afficherSql: true);
```
**À observer** : le SQL (une sous-requête `SELECT TOP(100)` **enveloppée** par le `WHERE` dans le premier cas) et les **résultats différents** (nombre de lignes < 100 pour le premier).

### Sans index / avec index et procédure stockée

Sans objet : c'est une question de sémantique.

### 🧪 TP 7.2.2 — « Trois ordres, trois résultats »

*Énoncé.* Écrivez les six permutations de `Where`, `OrderBy`, `Take` (`Where` avant ou après), notez celles qui retournent 100 lignes opérationnelles triées et les autres.

<details><summary>Correction</summary>

Seule l'ordre **Where → OrderBy → Take** (ou OrderBy → Where → Take, équivalent) donne « les 100 premiers opérationnels ». `Take` avant `Where` donne « parmi les 100 premiers, ceux qui sont opérationnels ». `Take` avant `OrderBy` trie seulement les lignes bornées (indéterminé sans tri préalable).
</details>

---

## 7.2.3 Le pattern spécification

**Axe concerné : ⚙️ .NET (organisation) + 🗄️ SQL (si bien écrit).**

### La notion

Une **spécification** encapsule un critère réutilisable sous forme d'`Expression<Func<T,bool>>` combinable (`Et`, `Ou`, `Non`). Avantages : critères métier nommés, testables, sans multiplication des méthodes de repository. Pièges : implémentation avec `Func` (évaluation en mémoire, voir 0.1) ; spécifications **qui accèdent à un service** ou appellent une méthode non traduisible ; reconstruire la même spécification **à chaque appel** (coût de construction de l'arbre).

### Dans quels cas on est concerné

Dépôts avec dizaines de méthodes `GetXxxByYyy`.

### Exemple sans l'amélioration

```csharp
// ❌ Spécification en Func : composable, mais exécutée côté client
public class SpecBad<T>(Func<T, bool> critere) { public Func<T, bool> Critere = critere; }
var s = new SpecBad<Starship>(v => v.IsOperational && v.CargoCapacity > 100);
await Mesurer("AVANT : spec Func", db => Task.FromResult(db.Starships.AsNoTracking().Where(s.Critere).Count()));
```

### Exemple avec l'amélioration

```csharp
public sealed class Spec<T>(Expression<Func<T, bool>> critere)
{
    public Expression<Func<T, bool>> Critere { get; } = critere;
    public Spec<T> Et(Spec<T> autre) => new(Critere.Et(autre.Critere));
    public Spec<T> Ou(Spec<T> autre) => new(Critere.Ou(autre.Critere));
}
public static class Vaisseaux
{
    public static readonly Spec<Starship> Operationnel = new(v => v.IsOperational);                 // construit UNE fois
    public static Spec<Starship> GrosPorteur(int mini) => new(v => v.CargoCapacity > mini);
}
var spec = Vaisseaux.Operationnel.Et(Vaisseaux.GrosPorteur(100));
await Mesurer("APRÈS : spec Expression", db => db.Starships.CountAsync(spec.Critere), afficherSql: true);
```
**À observer** : `WHERE … AND …` dans le SQL ; le **coût de reconstruction** (comparez 10 000 `GrosPorteur(100)` construits à chaque appel avec une spécification statique) en C# seul (sans SQL).

### Sans index / avec index

La spécification n'agit pas sur les index ; elle doit seulement produire des prédicats **sargables** (2.9).

### Comparaison avec une procédure stockée

Une procédure fige **un** critère ; la spécification en compose plusieurs à l'exécution (plus souple, plus de formes de plan).

### 🧪 TP 7.2.3 — « La spec qui fuit »

*Énoncé.* Ajoutez une spécification `NomContient(string s)` écrite avec `Func` par erreur et montrez qu'elle charge toute la table ; corrigez-la et vérifiez la traduction.

<details><summary>Correction</summary>

Avec `Func`, le SQL n'a pas de `WHERE` ; avec `Expression<Func<Starship,bool>> v => v.Name.Contains(s)`, il a un `LIKE`. Test automatisé : `ToQueryString()` doit contenir `WHERE`.
</details>

---

## 7.2.4 Tris dynamiques

**Axe concerné : 🗄️ SQL (tri dans la base, index) + ⚙️ .NET.**

### La notion

« Trier par la colonne choisie par l'utilisateur » : `OrderBy` avec une **expression construite** sur la propriété demandée, ou `EF.Property<T>(x, "Nom")`. Les mauvaises solutions : trier **en mémoire** après `ToList()`, ou par **réflexion** (`GetProperty(...).GetValue(x)`, non traduisible). Toujours **valider** le nom (liste blanche) : un nom de propriété inconnu lève une exception.

### Dans quels cas on est concerné

Grilles triables côté serveur.

### Exemple sans l'amélioration

```csharp
// ❌ Tri par réflexion, en mémoire, après avoir tout chargé
await Mesurer("AVANT : tri mémoire", async db =>
{
    var l = await db.Inhabitants.AsNoTracking().Where(i => i.PlanetId < 20_000).ToListAsync();
    var prop = typeof(Inhabitant).GetProperty("Credits")!;
    return l.OrderByDescending(x => prop.GetValue(x)).Take(50).Count();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Tri typé, traduit en ORDER BY
await Mesurer("APRÈS : EF.Property", async db =>
    (await db.Inhabitants.AsNoTracking().Where(i => i.PlanetId < 20_000)
        .OrderByDescending(i => EF.Property<decimal>(i, "Credits")).ThenBy(i => i.Id).Take(50).ToListAsync()).Count, afficherSql: true);

// ✔ Version générique (le type est découvert par réflexion UNE fois, la requête reste traduite)
public static IOrderedQueryable<T> TrierPar<T>(IQueryable<T> q, string prop, bool desc)
{
    var x = Expression.Parameter(typeof(T), "x");
    var membre = Expression.Property(x, prop);                                   // lève si la propriété n'existe pas
    var lambda = Expression.Lambda(membre, x);
    var appel = Expression.Call(typeof(Queryable), desc ? "OrderByDescending" : "OrderBy", [typeof(T), membre.Type], q.Expression, Expression.Quote(lambda));
    return (IOrderedQueryable<T>)q.Provider.CreateQuery<T>(appel);
}
```
**À observer** : `ORDER BY [Credits] DESC, [Id]` ; les lignes lues (50 contre tout le résultat).

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Inhabitants_Planet_Credits ON dbo.Inhabitants (PlanetId, Credits DESC, Id);
-- le tri par Credits au sein d'une plage de PlanetId reste un tri (plage), mais sur un jeu réduit ; avec PlanetId = 12 (égalité) l'index fournit l'ordre
DROP INDEX IX_Lab_Inhabitants_Planet_Credits ON dbo.Inhabitants;
```

### Comparaison avec une procédure stockée

Le tri dynamique en T-SQL passe par du SQL dynamique ou des `CASE WHEN @col = '…' THEN …` (qui empêchent l'usage d'index) : le LINQ généré est plus sûr et plus performant.

### 🧪 TP 7.2.4 — « Colonnes autorisées »

*Énoncé.* Écrivez `Trier(q, string col, bool desc)` limitée aux colonnes `Credits`, `BirthYear`, `LastName` ; toute autre valeur lève `ArgumentException`. Montrez le SQL des trois colonnes.

<details><summary>Correction</summary>

```csharp
static readonly HashSet<string> Autorisees = ["Credits", "BirthYear", "LastName"];
static IOrderedQueryable<Inhabitant> Trier(IQueryable<Inhabitant> q, string col, bool desc)
{
    if (!Autorisees.Contains(col)) throw new ArgumentException($"Tri non autorisé : {col}");
    return TrierPar(q, col, desc);
}
```
La liste blanche protège contre le tri sur une colonne non indexée coûteuse (déni de service) autant que contre l'injection.
</details>

---

## 7.2.5 Éviter `Expression.Invoke`

**Axe concerné : ⚙️ .NET (traduction) — *(comportement à vérifier selon la version)*.**

### La notion

`Expression.Invoke(autreLambda, argument)` crée un **appel de lambda** dans l'arbre. Selon la version, EF peut l'**aplatir** ou échouer (*could not be translated*). La méthode sûre pour réutiliser une lambda est le **remplacement de paramètre** (7.2.1), qui produit un arbre « à plat » que tout fournisseur comprend.

### Dans quels cas on est concerné

Combinaison de prédicats écrite « naïvement » avec `Invoke`.

### Exemple (à tester)

```csharp
Expression<Func<Trade, bool>> a = t => t.GoodId < 100;
Expression<Func<Trade, bool>> b = t => t.CompanyId < 100;
var p = Expression.Parameter(typeof(Trade), "t");
var avecInvoke = Expression.Lambda<Func<Trade, bool>>(Expression.AndAlso(Expression.Invoke(a, p), Expression.Invoke(b, p)), p);
try { Console.WriteLine(db.Trades.Where(avecInvoke).ToQueryString()); }
catch (Exception e) { Console.WriteLine(e.GetType().Name + " : " + e.Message.Split('\n')[0]); }
Console.WriteLine(db.Trades.Where(a.Et(b)).ToQueryString());       // ✔ remplacement de paramètre (7.2.1)
```
**À observer** : l'erreur ou le SQL pour la version `Invoke` ; le SQL de la version `Et`.

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 7.2.5 — « Reproduire l'échec »

*Énoncé.* Selon votre version, `Invoke` fonctionne-t-il ? Notez la version d'EF Core et le résultat. Écrivez un test qui **échoue** si quelqu'un réintroduit `Invoke` dans le code de filtres.

<details><summary>Correction</summary>

Test : `Assert.DoesNotContain("Invoke", expression.ToString())` sur les prédicats produits par vos helpers. Documentez la version testée : le comportement a évolué avec les versions d'EF Core.
</details>

---

# 7.3 Pagination avancée

## 7.3.1 Pagination par clé (keyset)

**Axe concerné : 🗄️ SQL (coût constant).** *Base : 1.2.*

### La notion

`Skip(n)` lit puis jette `n` lignes : le coût **croît** avec la profondeur. La pagination par clé mémorise la **dernière clé** de la page précédente et filtre `clé > dernière` : coût constant, à condition d'un **index sur l'ordre de tri**. Pour une clé composée (`TradedAt`, `Id`), on écrit la comparaison de tuple à la main.

### Dans quels cas on est concerné

Parcours séquentiel d'un grand ensemble (export, synchronisation, défilement infini).

### Exemple sans l'amélioration

```csharp
// ❌ Page 100 000 de 50 lignes sur 6 M d'échanges, tri par date
await Mesurer("AVANT : Skip(5 000 000)", async db =>
    (await db.Trades.AsNoTracking().OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Skip(5_000_000).Take(50).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Keyset : on part d'une position connue
var (dateRef, idRef) = (new DateTime(2025, 6, 1), 4_500_000L);
await Mesurer("APRÈS : keyset", async db =>
    (await db.Trades.AsNoTracking()
        .Where(t => t.TradedAt > dateRef || (t.TradedAt == dateRef && t.Id > idRef))
        .OrderBy(t => t.TradedAt).ThenBy(t => t.Id).Take(50).ToListAsync()).Count, afficherSql: true);
```
**À observer** : lectures qui **explosent** avec l'offset contre un coût constant ; **sans index** `(TradedAt, Id)`, le keyset lui-même scanne.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
-- sans index : les deux versions scannent et trient 6 M de lignes
CREATE INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades (TradedAt, Id);
-- avec index : keyset = Seek + 50 lignes ; offset = lecture de 5 000 050 entrées d'index (sans Sort mais coûteuse)
DROP INDEX IX_Lab_Trades_TradedAt_Id ON dbo.Trades;
```

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_PageEchanges @Date datetime2, @Id bigint, @Taille int
AS BEGIN SET NOCOUNT ON;
  SELECT TOP (@Taille) Id, MarketId, CompanyId, GoodId, TradeType, Quantity, UnitPrice, TradedAt
  FROM dbo.Trades WHERE TradedAt > @Date OR (TradedAt = @Date AND Id > @Id) ORDER BY TradedAt, Id; END
```
Même plan que le LINQ **avec** l'index ; sans index, la procédure ne fait pas mieux.

### 🧪 TP 7.3.1 — « Parcourir 6 M d'échanges »

*Énoncé.* Parcourez les 100 premières pages de 10 000 lignes par `Skip` puis par keyset, avec et sans l'index. Relevez le temps de la dernière page.

<details><summary>Correction</summary>

Sans index, les deux versions sont lentes ; avec l'index, l'offset ralentit linéairement et le keyset reste plat. L'index est la condition du keyset, pas un bonus.
</details>

---

## 7.3.2 Pagination sans `COUNT`

**Axe concerné : 🗄️ SQL (un parcours évité).**

### La notion

Afficher « page suivante ? » ne demande pas le total : on demande **`Take(taille + 1)`** et on regarde si une ligne de plus est revenue. Le `COUNT(*)` d'un filtre peut coûter plus cher que la page elle-même.

### Dans quels cas on est concerné

Listes à défilement infini, API sans total.

### Exemple sans l'amélioration

```csharp
// ❌ Compter tout pour savoir s'il y a une page suivante
await Mesurer("AVANT : Count + page", async db =>
{
    var q = db.Trades.AsNoTracking().Where(t => t.TradeType == 1 && t.Quantity > 400);
    var total = await q.CountAsync();
    var page = await q.OrderBy(t => t.Id).Take(50).ToListAsync();
    return $"total={total:N0} aSuivante={total > 50}";
}, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : Take(n+1)", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.TradeType == 1 && t.Quantity > 400).OrderBy(t => t.Id).Take(51).ToListAsync();
    return $"aSuivante={l.Count > 50}";
}, afficherSql: true);
```
**À observer** : une requête contre deux ; les lectures (`COUNT` parcourt tout, `TOP 51` s'arrête vite).

### Sans index / avec index

Le `TOP 51` ordonné par `Id` lit la clé clusterisée dans l'ordre : s'arrête très tôt ; le `COUNT` reste coûteux avec ou sans index sur `Quantity`.

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 7.3.2 — « Page suivante / précédente »

*Énoncé.* Retournez à la fois la page et deux indicateurs (`aSuivante`, `aPrecedente`) en une seule requête, sans `COUNT`.

<details><summary>Correction</summary>

Pour `aPrecedente`, utilisez le curseur (la clé de la première ligne de la page) : `Where(clé < premièreClé)` avec `Any()`. Deux petites requêtes à coût constant (ou une seule pour `n+1` et une pour `Any`).
</details>

---

## 7.3.3 Comptage approché

**Axe concerné : 🗄️ SQL (coût du `COUNT` exact).**

### La notion

Un `COUNT(*)` exact parcourt la table (ou le plus petit index). Pour **afficher un ordre de grandeur** (« environ 6 000 000 résultats »), on peut lire des **statistiques** (`sys.dm_db_partition_stats`) ou **échantillonner** (`TABLESAMPLE`). C'est approché, pas exact.

### Dans quels cas on est concerné

Tableaux de bord et listes où un total exact n'est pas indispensable.

### Exemple sans l'amélioration

```csharp
await Mesurer("AVANT : Count exact", db => db.Trades.CountAsync(), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Total de la table via les métadonnées : instantané
await Mesurer("APRÈS : partition_stats", async db =>
    (await db.Database.SqlQuery<long>($"SELECT SUM(row_count) AS Value FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('dbo.Trades') AND index_id IN (0, 1)").ToListAsync()).Single());

// ✔ Compte filtré approché par échantillon (1 %), à multiplier ×100
await Mesurer("APRÈS : TABLESAMPLE", async db =>
    100 * (await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Trades TABLESAMPLE (1 PERCENT) WHERE TradeType = 1 AND Quantity > 400").ToListAsync()).Single());
```
**À observer** : *logical reads* (6 M lus contre quelques pages / 1 % des pages) ; l'**écart** entre approché et exact (calculez-le) ; le fait que l'échantillon varie d'une exécution à l'autre.

### Sans index / avec index

Un index étroit réduit le coût du `COUNT` exact (moins de pages à lire) ; il n'en fait pas un coût constant.

### Comparaison avec une procédure stockée

Sans objet.

### 🧪 TP 7.3.3 — « Quelle erreur accepte-t-on ? »

*Énoncé.* Exécutez le `TABLESAMPLE` 20 fois et donnez l'écart-type relatif par rapport au `COUNT` exact. À partir de quelle taille de résultat l'approximation est-elle acceptable pour afficher « ≈ N » ?

<details><summary>Correction</summary>

L'erreur relative diminue avec la taille du résultat (plus de lignes échantillonnées) et augmente pour les filtres très sélectifs. Pour des filtres rares, l'échantillon peut renvoyer 0 : dans ce cas il faut un `COUNT` exact.
</details>

---

# 7.4 Requêtes ensemblistes évoluées

## 7.4.1 `NOT EXISTS`, `LEFT JOIN … IS NULL`, `NOT IN`

**Axe concerné : 🗄️ SQL (anti-jointure) + justesse (`NULL`).**

### La notion

Trois écritures pour « les éléments de A sans correspondant dans B » :

| Écriture | SQL | Remarque |
|---|---|---|
| `!a.Bs.Any()` | `NOT EXISTS` | **la plus sûre**, bien optimisée |
| `GroupJoin … DefaultIfEmpty … where b == null` | `LEFT JOIN … WHERE b.Id IS NULL` | équivalente, plus verbeuse |
| `!bIds.Contains(a.Id)` | `NOT IN (SELECT …)` | **piège** : si la sous-requête contient un `NULL`, `NOT IN` ne retourne **aucune ligne** |

### Dans quels cas on est concerné

Anti-jointure, détection d'orphelins, « sans … ».

### Exemple sans l'amélioration

```csharp
// ❌ NOT IN sur une colonne nullable : habitants sans faction → NULL dans la liste
await Mesurer("AVANT : NOT IN (colonne nullable)", async db =>
    await db.Factions.Where(f => f.Id <= 100_000 && !db.Inhabitants.Select(i => i.FactionId).Contains(f.Id)).CountAsync(), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS (1) : NOT EXISTS", async db =>
    await db.Factions.Where(f => f.Id <= 100_000 && !db.Inhabitants.Any(i => i.FactionId == f.Id)).CountAsync(), afficherSql: true);

await Mesurer("APRÈS (2) : LEFT JOIN … IS NULL", async db =>
    await db.Factions.Where(f => f.Id <= 100_000)
        .GroupJoin(db.Inhabitants, f => f.Id, i => i.FactionId, (f, ins) => new { f, ins })
        .SelectMany(x => x.ins.DefaultIfEmpty(), (x, i) => new { x.f, i }).Where(x => x.i == null).CountAsync(), afficherSql: true);
```
**À observer** : le SQL (`NOT IN` accompagné ou non de tests `IS NULL` ajoutés par EF) ; **les résultats** des trois (doivent être égaux — sinon le `NOT IN` a été piégé par les `NULL`) ; les plans (anti-semi-jointure).

### Sans index / avec index

`Inhabitants.FactionId` possède un index (FK) : `NOT EXISTS` fait un *Anti Semi Join* sur cet index. Supprimez-le temporairement dans un test (ne le faites pas sur la base d'origine) : l'anti-jointure devient un hachage sur la table entière.

### Comparaison avec une procédure stockée

Aucune différence : mêmes opérateurs de plan.

### 🧪 TP 7.4.1 — « Le NOT IN traître »

*Énoncé.* Reproduisez le cas où `NOT IN` retourne **zéro ligne** à cause d'un `NULL` (écrivez le SQL à la main), puis montrez ce que génère EF Core pour la même expression LINQ.

<details><summary>Correction</summary>

```sql
SELECT COUNT(*) FROM dbo.Factions f WHERE f.Id <= 100000 AND f.Id NOT IN (SELECT FactionId FROM dbo.Inhabitants);   -- 0 si un FactionId est NULL
SELECT COUNT(*) FROM dbo.Factions f WHERE f.Id <= 100000 AND NOT EXISTS (SELECT 1 FROM dbo.Inhabitants i WHERE i.FactionId = f.Id);
```
EF ajoute des conditions pour reproduire la sémantique C# (voir le SQL réel) ; selon la version, il peut générer un `NOT EXISTS`. Retenez : **n'écrivez pas `Contains` négatif sur une colonne nullable**.
</details>

---

## 7.4.2 Top N par groupe

**Axe concerné : 🗄️ SQL (`APPLY`, fenêtrage, index).** *Base : 2.13.*

### La notion

Quand les groupes sont **peu nombreux mais gros** (ici, les espèces par `Classification`), la meilleure stratégie est une **sous-requête corrélée avec `TOP(N)` par groupe** (`CROSS APPLY`) avec un index qui donne l'ordre ; `ROW_NUMBER()` trie **tout**. Quand les groupes sont **nombreux et petits** (2.13), c'est l'inverse.

### Dans quels cas on est concerné

« Les N plus … de chaque catégorie » sur des catégories à faible cardinalité.

### Exemple sans l'amélioration

```csharp
// ❌ Tout rapatrier puis grouper
await Mesurer("AVANT : GroupBy en mémoire", async db =>
{
    var l = await db.Species.AsNoTracking().Select(s => new { s.Classification, s.Id, s.AverageLifespan }).ToListAsync();
    return l.GroupBy(x => x.Classification).SelectMany(g => g.OrderByDescending(x => x.AverageLifespan).Take(3)).Count();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ APPLY : pour chaque classification (peu nombreuses), 3 lignes
await Mesurer("APRÈS (1) : SelectMany + Take (APPLY)", async db =>
    (await db.Species.Select(s => s.Classification).Distinct()
        .SelectMany(c => db.Species.Where(s => s.Classification == c).OrderByDescending(s => s.AverageLifespan).ThenBy(s => s.Id).Take(3))
        .ToListAsync()).Count, afficherSql: true);

// ✔ ROW_NUMBER
await Mesurer("APRÈS (2) : ROW_NUMBER", async db =>
    (await db.Species.FromSql($"""
        SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY Classification ORDER BY AverageLifespan DESC, Id) AS rn FROM dbo.Species) x WHERE rn <= 3
        """).AsNoTracking().ToListAsync()).Count, afficherSql: true);
```
**À observer** : `CROSS APPLY (SELECT TOP(3) …)` ; dans le plan, **Sort** sur 1 M de lignes pour `ROW_NUMBER` contre **seek** par classification pour `APPLY` avec l'index.

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Species_Class_Life ON dbo.Species (Classification, AverageLifespan DESC, Id);
-- APPLY : un Seek + 3 lignes par classification ; ROW_NUMBER : peut aussi éviter le Sort grâce à l'ordre de l'index
DROP INDEX IX_Lab_Species_Class_Life ON dbo.Species;
```

### Comparaison avec une procédure stockée

Procédure `ROW_NUMBER` : même plan que (2) ; elle ne gagne pas sur (1) avec l'index.

### 🧪 TP 7.4.2 — « Quel groupe pour quelle méthode ? »

*Énoncé.* Mesurez (1) APPLY et (2) ROW_NUMBER pour : `Classification` (peu de groupes) et `HomeworldId` (très nombreux petits groupes). Concluez.

<details><summary>Correction</summary>

Peu de gros groupes → APPLY (+ index) gagne ; beaucoup de petits groupes → `ROW_NUMBER` (un seul parcours trié) rattrape ou dépasse car l'APPLY multiplie les petits seeks. Il n'existe pas de meilleure méthode universelle : **la cardinalité des groupes décide**.
</details>

---

## 7.4.3 Sous-requêtes corrélées et jointures

**Axe concerné : 🗄️ SQL (choix de l'optimiseur).**

### La notion

Une navigation dans un `Select` (« le dernier marché de chaque planète ») devient une **sous-requête corrélée** (`OUTER APPLY (SELECT TOP 1 …)`). L'optimiseur sait souvent la transformer en jointure ; ce n'est pas garanti. Écrire explicitement un `GroupBy` + `Join` donne un plan différent — pas toujours meilleur.

### Dans quels cas on est concerné

« Dernier/premier/meilleur élément lié » par parent.

### Exemple sans l'amélioration

```csharp
// ❌ N+1 : une requête par planète
await Mesurer("AVANT : N+1", async db =>
{
    var planetes = await db.Planets.AsNoTracking().Where(p => p.Id <= 2_000).ToListAsync();
    var n = 0;
    foreach (var p in planetes)
        n += (await db.Markets.AsNoTracking().Where(m => m.PlanetId == p.Id).OrderByDescending(m => m.OpenedOn).Select(m => m.Name).FirstOrDefaultAsync()) is null ? 0 : 1;
    return n;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Sous-requête corrélée dans un Select : une requête
await Mesurer("APRÈS (1) : corrélée", async db =>
    (await db.Planets.Where(p => p.Id <= 2_000)
        .Select(p => new { p.Name, Dernier = p.Markets.OrderByDescending(m => m.OpenedOn).Select(m => m.Name).FirstOrDefault() }).ToListAsync()).Count, afficherSql: true);

// ✔ Jointure explicite avec agrégat
await Mesurer("APRÈS (2) : GroupBy + Join", async db =>
{
    var dernier = db.Markets.Where(m => m.PlanetId <= 2_000).GroupBy(m => m.PlanetId).Select(g => new { PlanetId = g.Key, Date = g.Max(m => m.OpenedOn) });
    return (await db.Planets.Where(p => p.Id <= 2_000).GroupJoin(dernier, p => p.Id, d => d.PlanetId, (p, d) => new { p.Name, d }).ToListAsync()).Count;
}, afficherSql: true);
```
**À observer** : `OUTER APPLY` contre `LEFT JOIN` sur sous-requête agrégée ; **comparez les plans** (ils peuvent être identiques : c'est une information).

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Markets_Planet_Date ON dbo.Markets (PlanetId, OpenedOn DESC) INCLUDE (Name);
-- « OUTER APPLY TOP 1 » devient un Seek + 1 ligne par planète
DROP INDEX IX_Lab_Markets_Planet_Date ON dbo.Markets;
```

### Comparaison avec une procédure stockée

Équivalent direct avec `OUTER APPLY`. Pas de différence de plan.

### 🧪 TP 7.4.3 — « Premier ou meilleur »

*Énoncé.* Pour chaque planète d'`Id ≤ 2 000`, retournez le nom du marché **le plus ancien** et son `TaxRate`. Écrivez la version corrélée et la version `ROW_NUMBER`. Quelle est la plus lisible ? la plus rapide avec l'index ?

<details><summary>Correction</summary>

La version corrélée (`OrderBy(...).Select(m => new { m.Name, m.TaxRate }).FirstOrDefault()`) est la plus lisible ; avec l'index `(PlanetId, OpenedOn) INCLUDE (Name, TaxRate)`, elle est aussi la plus rapide (seek par planète, pas de tri).
</details>

---

## 7.4.4 Agrégats corrélés multiples

**Axe concerné : 🗄️ SQL (autant de sous-requêtes que d'agrégats).**

### La notion

`Select(c => new { c.Name, c.Starships.Count(), c.Trades.Count(), c.Trades.Max(...) })` génère **une sous-requête corrélée par agrégat**, évaluée pour **chaque société**. Pour peu de lignes, c'est acceptable ; sur des milliers, chaque agrégat est un parcours supplémentaire. Alternative : **pré-agréger** par clé avec un `GroupBy` par table, puis assembler.

### Dans quels cas on est concerné

Tableaux de bord « une ligne par entité avec plusieurs totaux ».

### Exemple sans l'amélioration

```csharp
await Mesurer("AVANT : 3 sous-requêtes corrélées", async db =>
    (await db.Companies.Where(c => c.Id <= 20_000)
        .Select(c => new { c.Name, Vaisseaux = c.Starships.Count(), Echanges = c.Trades.Count(), Max = c.Trades.Max(t => (decimal?)t.UnitPrice) }).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Pré-agrégation, assemblage en mémoire (3 requêtes simples)
await Mesurer("APRÈS : pré-agrégation", async db =>
{
    var soc   = await db.Companies.AsNoTracking().Where(c => c.Id <= 20_000).Select(c => new { c.Id, c.Name }).ToListAsync();
    var nav   = (await db.Starships.Where(s => s.CompanyId <= 20_000).GroupBy(s => s.CompanyId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
    var ech   = (await db.Trades.Where(t => t.CompanyId <= 20_000).GroupBy(t => t.CompanyId).Select(g => new { g.Key, N = g.Count(), Max = g.Max(t => t.UnitPrice) }).ToListAsync()).ToDictionary(x => x.Key);
    return soc.Count(c => nav.ContainsKey(c.Id) || ech.ContainsKey(c.Id));
});
```
**À observer** : 1 requête à 3 sous-requêtes corrélées contre 3 requêtes à un seul parcours ; **les plans** (l'optimiseur peut déjà fusionner les agrégats : constatez-le) ; *logical reads* totaux.

### Sans index / avec index

Les trois agrégats utilisent les index de FK (`IX_Starships_CompanyId`, `IX_Trades_CompanyId`) ; un index couvrant `(CompanyId) INCLUDE (UnitPrice)` réduit l'agrégat sur `Trades`.

### Comparaison avec une procédure stockée

Une procédure peut faire les trois agrégats dans des CTE jointes : plan semblable à la pré-agrégation.

### 🧪 TP 7.4.4 — « Quand la corrélée suffit »

*Énoncé.* Mesurez la version corrélée et la pré-agrégée pour 100, 1 000, 20 000 sociétés. À partir de quelle taille la pré-agrégation devient-elle plus rapide ?

<details><summary>Correction</summary>

Pour 100 sociétés, la corrélée gagne (peu de seeks, une seule requête) ; la pré-agrégation lit les index sur plus de lignes que nécessaire. Le point de croisement dépend des index et du réseau : **mesurez**.
</details>

---

## 7.4.5 `Distinct` → `EXISTS`

**Axe concerné : 🗄️ SQL.**

### La notion

Une jointure qui **multiplie** les lignes du parent (une par enfant) suivie de `Distinct` fait **dédoublonner** le résultat. Quand on veut seulement « les parents qui ont au moins un enfant », `Any()` (→ `EXISTS`) évite ce travail (voir 1.7).

### Dans quels cas on est concerné

Jointure + `Distinct()` pour « filtrer par existence ».

### Exemple sans l'amélioration

```csharp
// ❌ Join puis Distinct : 120 000 marchés noirs → dédoublonnage
await Mesurer("AVANT : Join + Distinct", async db =>
    (await db.Planets.Join(db.Markets.Where(m => m.IsBlackMarket), p => p.Id, m => m.PlanetId, (p, m) => p.Id).Distinct().CountAsync()), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : Any", async db =>
    await db.Planets.CountAsync(p => p.Markets.Any(m => m.IsBlackMarket)), afficherSql: true);
```
**À observer** : `DISTINCT` + jointure contre `EXISTS` ; le plan (*Hash Match Aggregate* contre *Semi Join*).

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Markets_Planet_Black ON dbo.Markets (PlanetId) WHERE IsBlackMarket = 1;
-- EXISTS : seek dans un index qui ne contient que les ~6 % de marchés noirs
DROP INDEX IX_Lab_Markets_Planet_Black ON dbo.Markets;
```

### Comparaison avec une procédure stockée

Aucune différence.

### 🧪 TP 7.4.5 — « Les planètes avec spatioport et marché noir »

*Énoncé.* Réécrivez `Planets.Join(Markets…).Where(p.HasSpaceport).Distinct()` en `Any`. Les résultats sont-ils égaux ? Quel est le plan ?

<details><summary>Correction</summary>

`db.Planets.Where(p => p.HasSpaceport && p.Markets.Any(m => m.IsBlackMarket))` : mêmes planètes, sans dédoublonnage ; le plan utilise un *Semi Join*.
</details>

---

## 7.4.6 `Contains` volumineux

**Axe concerné : 🗄️ SQL (paramètres) + ⚙️ .NET.** *Base : 2.6.*

### La notion

Au-delà de quelques milliers d'identifiants, `Contains` atteint les limites du fournisseur (2 100 paramètres) ou produit un SQL énorme. Alternatives : lots (`Chunk`), **paramètre table (TVP)**, table temporaire alimentée par `SqlBulkCopy` puis jointure.

### Dans quels cas on est concerné

Listes d'ids de dizaines de milliers d'éléments (résultat d'un autre système, import).

### Exemple sans l'amélioration

```csharp
var ids = Enumerable.Range(1, 50_000).ToList();
await Mesurer("AVANT : Contains 50 000", db => db.Goods.CountAsync(g => ids.Contains(g.Id)));           // selon la version : SQL géant, plan lourd, voire erreur
```

### Exemple avec l'amélioration

```sql
CREATE TYPE dbo.Lab_IdList AS TABLE (Id int NOT NULL PRIMARY KEY);
GO
CREATE OR ALTER PROCEDURE dbo.Lab_BiensParIds @Ids dbo.Lab_IdList READONLY
AS BEGIN SET NOCOUNT ON; SELECT g.Id, g.Sku, g.Name, g.Category, g.BasePrice, g.WeightKg, g.IsContraband, g.IsPerishable FROM dbo.Goods g JOIN @Ids i ON i.Id = g.Id; END
```
```csharp
// ✔ TVP : un paramètre, quel que soit le nombre d'ids
await Mesurer("APRÈS : TVP + jointure", async db =>
{
    var table = new DataTable(); table.Columns.Add("Id", typeof(int)); foreach (var id in ids) table.Rows.Add(id);
    var p = new SqlParameter("@Ids", SqlDbType.Structured) { TypeName = "dbo.Lab_IdList", Value = table };
    return (await db.Goods.FromSqlRaw("SELECT g.* FROM dbo.Goods g JOIN @Ids i ON i.Id = g.Id", p).AsNoTracking().ToListAsync()).Count;      // composable
}, afficherSql: true);

// ✔ Lots
await Mesurer("APRÈS : Chunk(1000)", async db =>
{ var n = 0; foreach (var lot in ids.Chunk(1_000)) n += await db.Goods.CountAsync(g => lot.Contains(g.Id)); return n; });
```
**À observer** : taille du SQL et nombre de paramètres ; le plan du `JOIN @Ids` (seek par id ; estimation du nombre de lignes de la variable table) ; temps de construction du `DataTable` (côté .NET).

### Sans index / avec index

La clé primaire `Goods.Id` fournit le seek ; pour une colonne non indexée (`Sku` n'a que son index unique), chaque id est un scan sans index.

### Comparaison avec une procédure stockée

La procédure `Lab_BiensParIds` avec TVP est **la** réponse SQL idiomatique ; EF Core ne la gère pas nativement (paramètre structuré à passer à la main, comme ci-dessus) : c'est un cas **légitime de sortie de LINQ** (chapitre 10).

### 🧪 TP 7.4.6 — « 50 000 identifiants »

*Énoncé.* Comparez : `Contains` direct, lots de 1 000, TVP, table temporaire (`SqlBulkCopy` dans `#ids` puis jointure) pour 50 000 ids. Remplissez temps / requêtes / taille du SQL.

<details><summary>Correction</summary>

Attendu : le TVP et la table temporaire donnent un plan stable et un SQL court ; les lots sont corrects et simples ; le `Contains` direct dépend de la version. Le critère de choix est la **simplicité** (lots) tant que le nombre de lots reste petit, puis le TVP.
</details>

---

# 7.5 Contrôle de l'exécution SQL

## 7.5.1 Intercepteur de commandes et hints

**Axe concerné : 🗄️ SQL (hints), ⚙️ .NET (intercepteur).**

### La notion

LINQ n'offre **aucun hint**. Un `DbCommandInterceptor` peut **modifier le texte SQL** juste avant l'envoi : ajouter `OPTION (RECOMPILE)`, `OPTION (OPTIMIZE FOR UNKNOWN)`, `OPTION (MAXDOP 1)`, ou un `WITH (NOLOCK)` (dangereux : lectures sales). On marque les requêtes concernées avec `TagWith` et l'intercepteur réagit au tag.

### Dans quels cas on est concerné

Requêtes dont le plan dépend de la valeur du paramètre (7.5.2), requêtes qui exigent un hint pour un plan stable.

### Exemple

```csharp
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
// dans NewDb : .AddInterceptors(new Indices())
```
```csharp
var l = await db.Planets.TagWith("recompile").Where(p => p.Population > 99_000_000_000).Select(p => p.Name).ToListAsync();
```
**À observer** : `OPTION (RECOMPILE)` à la fin du SQL (journal) ; le plan **recompilé à chaque exécution** (coût CPU de compilation, mais plan adapté à la valeur) ; ne pas oublier que le tag est un **commentaire** : `TagWith` placé au début du SQL.

### Sans index / avec index et procédure stockée

Les hints se comparent aux **hints de procédure** (`WITH RECOMPILE` sur la procédure, `OPTION (…)` dans son corps) : mêmes effets ; avantage de l'intercepteur : un seul point de contrôle.

### 🧪 TP 7.5.1 — « Un hint, un tag »

*Énoncé.* Ajoutez un tag `maxdop1` qui ajoute `OPTION (MAXDOP 1)`. Vérifiez dans le plan que le parallélisme a disparu pour une requête qui scanne 6 M de lignes, et mesurez l'effet sur le temps et le CPU serveur.

<details><summary>Correction</summary>

Sans parallélisme, le CPU total baisse un peu (moins de coordination) mais la **durée** augmente (un seul cœur). Le hint est un outil de **diagnostic** et de contrôle de la contention, pas une optimisation générale.
</details>

---

## 7.5.2 *Parameter sniffing*

**Axe concerné : 🗄️ SQL (plans réutilisés). ** *Base : 2.5.*

### La notion

Le plan est compilé pour la **première** valeur de paramètre et **réutilisé**. Si la distribution est très asymétrique, le plan peut être catastrophique pour d'autres valeurs. Stratégies : `OPTION (RECOMPILE)` (plan par exécution), `OPTIMIZE FOR UNKNOWN` (plan « moyen »), **constante** (plan par valeur), **requêtes séparées** par profil (étroit / large), index mieux conçu qui rend le plan unique.

### Dans quels cas on est concerné

Colonnes très asymétriques (`Planets.Population`), seuils de plage, tailles de page variables.

### Exemple

```sql
CREATE INDEX IX_Lab_Planets_Population ON dbo.Planets (Population);       -- sans INCLUDE : lookups
```
```csharp
long etroit = 99_000_000_000, large = 1_000_000_000;     // calibrez avec COUNT
IQueryable<Planet> Q(StarWarsContext db, long seuil, string tag) => db.Planets.TagWith(tag).AsNoTracking().Where(p => p.Population > seuil);

foreach (var tag in new[] { "standard", "recompile", "unknown" })
{
    // DBCC FREEPROCCACHE; avant chaque série
    await Mesurer($"{tag} : étroit puis large", async db =>
    {
        var a = (await Q(db, etroit, tag).ToListAsync()).Count;
        var b = (await Q(db, large, tag).ToListAsync()).Count;
        return $"{a} + {b}";
    });
}
```
**À observer** : `logical reads` de la 2ᵉ requête selon la stratégie ; *Estimated vs Actual rows* dans le plan réel ; avec `recompile`, deux plans différents ; avec `unknown`, un plan unique « moyen » (rarement optimal pour les extrêmes).

### Sans index / avec index

Sans l'index, le plan est un scan dans tous les cas (pas de sniffing). Avec un index **couvrant** (`INCLUDE (Name, …)`), le plan seek est bon pour toutes les valeurs : l'index mieux conçu **supprime** le problème.

### Comparaison avec une procédure stockée

Une procédure **souffre du même sniffing** ; on ajoute `WITH RECOMPILE` ou `OPTION (RECOMPILE)` dans son corps. L'avantage du LINQ + intercepteur : on peut choisir **par requête** sans toucher à la base.

### 🧪 TP 7.5.2 — « Le tableau de décision »

*Énoncé.* Remplissez : stratégie / lectures requête étroite / lectures requête large / coût CPU de compilation. Concluez quelle stratégie choisir pour une API exposant `Population > x` à l'utilisateur.

<details><summary>Correction</summary>

`RECOMPILE` coûte un peu de CPU par exécution mais garantit un plan adapté ; c'est le bon choix pour une requête **peu fréquente** à seuil libre. Pour une requête **très fréquente**, préférer un index couvrant ou deux requêtes distinctes (cas étroit/large).
</details>

---

## 7.5.3 Timeouts et annulation

**Axe concerné : 🗄️ SQL (ressources libérées) + robustesse.**

### La notion

`CommandTimeout` (défaut 30 s) interrompt une commande trop longue ; `CancellationToken` permet d'**annuler** depuis l'application (utilisateur qui quitte, délai global). Sans annulation propagée, **la requête continue de s'exécuter sur le serveur** même si l'appelant n'écoute plus. Le jeton doit être passé à **chaque** appel asynchrone (`ToListAsync(ct)`).

### Dans quels cas on est concerné

Requêtes longues exposées à des utilisateurs, requêtes d'administration.

### Exemple sans l'amélioration

```csharp
// ❌ Requête lourde sans annulation : l'abandon côté client ne la tue pas
var lourde = db.Trades.Where(t => db.Trades.Any(u => u.GoodId == t.GoodId && u.Quantity > t.Quantity)).Select(t => t.Id);
```

### Exemple avec l'amélioration

```csharp
// ✔ Annulation après 2 secondes
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var sw = Stopwatch.StartNew();
try { await lourde.CountAsync(cts.Token); }
catch (OperationCanceledException) { Console.WriteLine($"annulée après {sw.ElapsedMilliseconds} ms"); }

// ✔ Timeout par contexte
db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));
try { await lourde.CountAsync(); } catch (Exception e) { Console.WriteLine(e.GetType().Name + " : " + e.Message.Split('\n')[0]); }
```
**À observer** : le temps d'annulation ; **côté SQL**, la requête **disparaît** de `sys.dm_exec_requests` après l'annulation (avant, elle y est) ; l'exception `OperationCanceledException` vs `SqlException` (timeout).

### Sans index / avec index

La requête de démonstration est volontairement coûteuse sans index adapté : un index `(GoodId, Quantity)` la rendrait rapide — l'annulation n'est qu'un **filet**.

### Comparaison avec une procédure stockée

Une procédure en cours d'exécution est annulée de la même façon ; les transactions ouvertes sont annulées par la session fermée.

### 🧪 TP 7.5.3 — « Propager le jeton »

*Énoncé.* Écrivez une méthode de service qui appelle trois requêtes en séquence en propageant un jeton, et vérifiez que l'annulation pendant la 2ᵉ requête empêche la 3ᵉ de partir.

<details><summary>Correction</summary>

Passez le jeton à **chaque** `ToListAsync(ct)` / `CountAsync(ct)` et appelez `ct.ThrowIfCancellationRequested()` entre les étapes pour les traitements purement .NET. Vérifiez avec `CompteurSql` qu'aucune 3ᵉ requête n'est émise.
</details>

---

# 7.6 Arbres d'expression

## 7.6.1 Construire une expression

**Axe concerné : ⚙️ .NET (cache de requêtes) + 🗄️ SQL.**

### La notion

`Expression.Property`, `Expression.Equal`, `Expression.Lambda`… permettent de construire une requête à partir de **données** (nom de colonne, opérateur). Règle d'or : **valeurs par fermeture** (7.1.1), **noms validés** (liste blanche), **types explicites**.

### Dans quels cas on est concerné

API de filtrage générique (`?champ=…&op=…&valeur=…`).

### Exemple

```csharp
static Expression<Func<T, bool>> Filtre<T>(string champ, string op, object valeur)
{
    var x = Expression.Parameter(typeof(T), "x");
    var membre = Expression.Property(x, champ);
    var v = Convert.ChangeType(valeur, membre.Type);
    var capt = Expression.Property(Expression.Constant(new Boite(v)), nameof(Boite.Valeur));         // fermeture : paramètre SQL
    var conv = Expression.Convert(capt, membre.Type);
    Expression test = op switch { "=" => Expression.Equal(membre, conv), ">" => Expression.GreaterThan(membre, conv), "<" => Expression.LessThan(membre, conv), _ => throw new ArgumentException(op) };
    return Expression.Lambda<Func<T, bool>>(test, x);
}
public sealed record Boite(object Valeur);
```
```csharp
Console.WriteLine(db.Inhabitants.Where(Filtre<Inhabitant>("Credits", ">", 200000m)).ToQueryString());
Console.WriteLine(db.Inhabitants.Where(Filtre<Inhabitant>("BirthYear", "<", 1500)).ToQueryString());
```
**À observer** : le SQL paramétré (`> @__Valeur_0` : *à vérifier* — si EF inline la valeur, utilisez le motif `Expression<Func<TV>> capt = () => valeur;` de 7.1.1) ; le nombre de plans pour 100 valeurs ; l'erreur sur une propriété inexistante.

### Sans index / avec index et procédure stockée

Le filtre généré obéit aux mêmes règles d'index que tout prédicat.

### 🧪 TP 7.6.1 — « Le filtre sûr »

*Énoncé.* Ajoutez à `Filtre` une liste blanche de champs par entité et un opérateur `contient` traduit en `Contains`. Vérifiez que les valeurs restent des paramètres.

<details><summary>Correction</summary>

Ajouter `if (!Autorises[typeof(T)].Contains(champ)) throw …` et pour `contient` : `Expression.Call(membre, typeof(string).GetMethod("Contains", [typeof(string)])!, conv)`. `ToQueryString()` doit afficher un paramètre dans le `LIKE`.
</details>

---

## 7.6.2 Réécrire un arbre (`ExpressionVisitor`)

**Axe concerné : ⚙️ .NET (réutilisation de règles métier dans la requête).**

### La notion

Un `ExpressionVisitor` parcourt un arbre et le **réécrit**. Cas courant : **inliner une règle métier** écrite sous forme d'expression (`Expression<Func<…>>` statique) dans les requêtes, au lieu d'appeler une **méthode** C# non traduisible. Le visiteur remplace l'appel de méthode par le corps de l'expression.

### Dans quels cas on est concerné

Règles métier réutilisées (« est un gros porteur ») dans plusieurs requêtes.

### Exemple sans l'amélioration

```csharp
public static class Regles { public static bool EstGrosPorteur(Starship s) => s.IsOperational && s.CargoCapacity > 100; }
// ❌ Non traduisible dans un Where
try { Console.WriteLine(db.Starships.Where(s => Regles.EstGrosPorteur(s)).ToQueryString()); }
catch (Exception e) { Console.WriteLine(e.Message.Split('\n')[0]); }
```

### Exemple avec l'amélioration

```csharp
public static class Regles2
{
    public static readonly Expression<Func<Starship, bool>> GrosPorteurExpr = s => s.IsOperational && s.CargoCapacity > 100;
    public static bool EstGrosPorteur(this Starship s) => GrosPorteurExpr.Compile()(s);          // usage en mémoire uniquement
}
public sealed class Developpeur : ExpressionVisitor
{
    protected override Expression VisitMethodCall(MethodCallExpression m)
    {
        if (m.Method.Name == nameof(Regles2.EstGrosPorteur) && m.Method.DeclaringType == typeof(Regles2))
        {
            var arg = Visit(m.Arguments[0])!;
            return new RemplacerParam(Regles2.GrosPorteurExpr.Parameters[0], arg).Visit(Regles2.GrosPorteurExpr.Body)!;
        }
        return base.VisitMethodCall(m);
    }
    private sealed class RemplacerParam(ParameterExpression de, Expression vers) : ExpressionVisitor
    { protected override Expression VisitParameter(ParameterExpression n) => n == de ? vers : n; }
}
static Expression<Func<T, bool>> Developper<T>(Expression<Func<T, bool>> e) => (Expression<Func<T, bool>>)new Developpeur().Visit(e)!;
```
```csharp
Console.WriteLine(db.Starships.Where(Developper<Starship>(s => s.EstGrosPorteur())).ToQueryString());      // ✔ WHERE IsOperational = 1 AND CargoCapacity > 100
```
**À observer** : l'erreur de traduction contre le SQL correct ; une seule définition de la règle, utilisable en SQL (via l'expression) **et** en mémoire (via la méthode).

### Sans index / avec index et procédure stockée

La règle développée est un prédicat ordinaire : sargable selon son écriture.

### 🧪 TP 7.6.2 — « Une règle, deux mondes »

*Énoncé.* Ajoutez une règle `EstRecent` sur `Market.OpenedOn` et utilisez-la dans une requête SQL et dans un filtre en mémoire. Écrivez un test qui garantit que les deux donnent le même résultat sur un échantillon.

<details><summary>Correction</summary>

Comparer `db.Markets.Where(Developper(m => m.EstRecent())).Take(1000)` et `liste.Where(m => m.EstRecent())` sur les mêmes données : le test détecte une divergence de sémantique SQL/C# (voir 0.7).
</details>

---

## 7.6.3 `IQueryExpressionInterceptor`

**Axe concerné : ⚙️ .NET (garde-fous globaux) + qualité des requêtes.**

### La notion

`IQueryExpressionInterceptor` reçoit l'**arbre LINQ complet** juste avant sa traduction : on peut l'**inspecter** (garde-fou, journal) ou le **réécrire** (ajout d'un filtre, d'un `AsNoTracking`). C'est le bon endroit pour imposer des règles à **toute** l'application : « pas de `Take` sans `OrderBy` », « pas de requête sans `TagWith` ».

### Dans quels cas on est concerné

Équipes qui veulent verrouiller les mauvaises pratiques sans revue de code.

### Exemple

```csharp
public sealed class GardeTakeSansOrderBy : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    { new Controleur().Visit(queryExpression); return queryExpression; }

    private sealed class Controleur : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (m.Method.DeclaringType == typeof(Queryable) && (m.Method.Name is "Take" or "Skip") && !ATri(m.Arguments[0]))
                throw new InvalidOperationException($"{m.Method.Name} sans OrderBy : résultat non déterministe.");
            return base.VisitMethodCall(m);
        }
        private static bool ATri(Expression e)
        {
            while (e is MethodCallExpression mc)
            {
                if (mc.Method.Name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending") return true;
                if (mc.Arguments.Count == 0) return false;
                e = mc.Arguments[0];
            }
            return false;
        }
    }
}
// dans NewDb : .AddInterceptors(new GardeTakeSansOrderBy())
```
```csharp
try { await db.Goods.Take(10).ToListAsync(); } catch (Exception e) { Console.WriteLine(e.Message); }      // lève
await db.Goods.OrderBy(g => g.Id).Take(10).ToListAsync();                                                  // passe
```
**À observer** : l'exception **avant** tout envoi SQL ; le surcoût sur la requête (visiteur à chaque *compilation*, donc négligeable en régime avec cache). Les interfaces de traduction plus profondes (`IMethodCallTranslatorPlugin`) sont des API **internes/avancées** : à n'utiliser qu'en connaissance de cause *(à vérifier selon la version)*.

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 7.6.3 — « Imposer un tag »

*Énoncé.* Écrivez un intercepteur qui **refuse** toute requête sans `TagWith`. Où détectez-vous le tag dans l'arbre ?

<details><summary>Correction</summary>

Cherchez un appel à `EntityFrameworkQueryableExtensions.TagWith` dans la chaîne de méthodes (`m.Method.Name == "TagWith"`). Attention aux requêtes internes d'EF (migrations, historique) : les exclure par `eventData.Context` ou par type d'entité.
</details>

---

# 7.7 Matérialisation et mémoire

## 7.7.1 Streaming et traitement par lots

**Axe concerné : 🧠 mémoire + ⚙️ .NET.** *Base : 0.3.*

### La notion

`AsAsyncEnumerable()` traite les lignes une à une. Pour un traitement **coûteux par ligne** (appel HTTP, calcul), on peut **regrouper** les lignes en paquets et les traiter **en parallèle** sans tout charger. Contrainte : le *DataReader* reste ouvert ; un consommateur lent allonge la requête côté SQL.

### Dans quels cas on est concerné

Exports vers un service externe, recalculs lourds.

### Exemple sans l'amélioration

```csharp
// ❌ Tout charger puis traiter
await Mesurer("AVANT : ToList puis traitement", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.GoodId < 50_000).ToListAsync();
    return l.Sum(t => Lourd(t));
});
static long Lourd(Trade t) { var h = 0L; for (var i = 0; i < 200; i++) h += (t.Id * 31 + i) % 7; return h; }
```

### Exemple avec l'amélioration

```csharp
// ✔ Flux + paquets de 10 000 traités en parallèle
await Mesurer("APRÈS : flux + paquets parallèles", async db =>
{
    var total = 0L; var paquet = new List<Trade>(10_000);
    async Task Vider() { var p = paquet.ToArray(); paquet.Clear(); total += await Task.Run(() => p.AsParallel().Sum(Lourd)); }
    await foreach (var t in db.Trades.AsNoTracking().Where(t => t.GoodId < 50_000).AsAsyncEnumerable())
    { paquet.Add(t); if (paquet.Count == 10_000) await Vider(); }
    if (paquet.Count > 0) await Vider();
    return total;
});
```
**À observer** : pic de mémoire (liste complète contre un paquet) ; CPU utilisés (`dotnet-counters`) ; durée de la requête SQL (la session reste active pendant tout le flux).

### Sans index / avec index et procédure stockée

Sans objet (le filtre `GoodId < 50000` utilise l'index de FK).

### 🧪 TP 7.7.1 — « Bon dimensionnement du paquet »

*Énoncé.* Faites varier la taille du paquet (100, 1 000, 10 000, 100 000). Tracez pic mémoire et temps. Quelle taille choisissez-vous ?

<details><summary>Correction</summary>

De petits paquets limitent la mémoire mais augmentent la synchronisation ; de grands paquets l'inverse. L'optimum se situe en général à quelques milliers de lignes ; mesurez avec **votre** traitement.
</details>

---

## 7.7.2 Colonnes volumineuses (`max` / binaire)

**Axe concerné : 🗄️ SQL (pages LOB) + 🧠 mémoire + réseau.**

### La notion

Les colonnes `nvarchar(max)` / `varbinary(max)` volumineuses sont stockées **hors ligne** (pages LOB). Les inclure par défaut dans chaque lecture d'entité coûte en lectures, en transfert et en mémoire. Remèdes : **projeter** sans la colonne, **séparer** le contenu volumineux dans une table liée (*table splitting* inverse : entité secondaire chargée à la demande), lire en **flux** (`GetStream`, `SequentialAccess`) côté ADO.NET.

### Dans quels cas on est concerné

Documents, images, JSON volumineux, journaux.

### Mise en place

```sql
CREATE TABLE dbo.Lab_Blob (Id int PRIMARY KEY, Titre nvarchar(50) NOT NULL, Contenu nvarchar(max) NOT NULL);
INSERT INTO dbo.Lab_Blob (Id, Titre, Contenu)
SELECT value, CONCAT(N'Doc ', value), REPLICATE(CAST(N'x' AS nvarchar(max)), 20000) FROM GENERATE_SERIES(1, 5000);       -- ~40 Ko par ligne, ~200 Mo au total
```
```csharp
public class LabBlob { public int Id { get; set; } public string Titre { get; set; } = ""; public string Contenu { get; set; } = ""; }
// Classe partielle : public DbSet<LabBlob> LabBlobs => Set<LabBlob>();   et   modelBuilder.Entity<LabBlob>().ToTable("Lab_Blob");
```

### Exemple sans l'amélioration

```csharp
// ❌ Lister les titres en chargeant les contenus
await MesurerReseau("AVANT : entité complète", async db =>
    (await db.LabBlobs.AsNoTracking().ToListAsync()).Select(b => b.Titre).Count());
```

### Exemple avec l'amélioration

```csharp
// ✔ Projection : la colonne LOB n'est pas lue
await MesurerReseau("APRÈS : projection", async db =>
    (await db.LabBlobs.AsNoTracking().Select(b => b.Titre).ToListAsync()).Count);
```
**À observer** : Mo reçus (centaines de Mo contre quelques Ko) ; *logical reads* **LOB** (`lob logical reads` dans `SET STATISTICS IO`) ; mémoire.

### Sans index / avec index

Un index non clusterisé `(Titre)` permet de lister les titres sans toucher aux pages LOB ; l'entité complète les ignorerait de toute façon.

### Comparaison avec une procédure stockée

Sans objet : la procédure renverrait aussi la colonne si elle est dans le `SELECT`.

### 🧪 TP 7.7.2 — « Lecture à la demande »

*Énoncé.* Écrivez `LireContenu(db, id)` qui ne lit le contenu **qu'à la demande** pour un document. Comparez avec un `Include` d'une entité séparée `LabBlobContenu` (même table, deux entités).

<details><summary>Correction</summary>

Modélisez `LabBlobContenu(Id, Contenu)` sur la même table (*table splitting*) et chargez-le par `Select(b => b.Contenu)` ou `Entry(b).Reference(...).Load()`. Dans les deux cas, les requêtes de liste ne touchent plus aux pages LOB.
</details>

---

## 7.7.3 Le tas des grands objets (LOH)

**Axe concerné : 🧠 mémoire / ⚙️ .NET (GC).**

### La notion

Les objets de plus de **85 000 octets** sont alloués dans le **LOH**, collecté uniquement lors des collections de **génération 2** (coûteuses). Une `List<T>` qui **grandit par doublement** réalloue de grands tableaux successifs, tous dans le LOH. Remèdes : **dimensionner à l'avance** (`new List<T>(capacité)`), traiter en **flux/paquets**, `ArrayPool<T>`.

### Dans quels cas on est concerné

Accumulation de centaines de milliers/millions d'éléments en mémoire.

### Exemple sans l'amélioration

```csharp
// ❌ Une liste de 5 M d'éléments qui grandit par doublement
await Mesurer("AVANT : List sans capacité", db =>
{
    var l = new List<long>(); for (long i = 0; i < 5_000_000; i++) l.Add(i);
    return Task.FromResult(l.Count);
});
```

### Exemple avec l'amélioration

```csharp
await Mesurer("APRÈS : capacité connue", db =>
{
    var l = new List<long>(5_000_000); for (long i = 0; i < 5_000_000; i++) l.Add(i);
    return Task.FromResult(l.Count);
});
// Avec EF : connaître la taille attendue (Count) avant d'accumuler
await Mesurer("Avec Count préalable", async db =>
{
    var n = await db.Goods.CountAsync(g => g.Id <= 500_000);
    var l = new List<Good>(n); await foreach (var g in db.Goods.AsNoTracking().Where(g => g.Id <= 500_000).AsAsyncEnumerable()) l.Add(g);
    return l.Count;
});
```
**À observer** : « Mo alloués » (≈ le double du total pour la liste sans capacité) ; `GC2` ; `GC.GetGCMemoryInfo()` (taille du LOH) ; `dotnet-counters` : `LOH Size`.

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 7.7.3 — « Voir le LOH »

*Énoncé.* Affichez la taille du LOH avant/après chaque variante avec `GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes`. Combien de collections de génération 2 chaque variante déclenche-t-elle ?

<details><summary>Correction</summary>

```csharp
var info = GC.GetGCMemoryInfo(); Console.WriteLine($"LOH : {info.GenerationInfo[3].SizeAfterBytes / 1_048_576.0:N1} Mo");
```
La variante sans capacité alloue des tableaux de 8, 16, 32… Mo successifs ; la variante avec capacité en alloue **un**. Le gain est surtout visible sur les **GC gen2** et la mémoire de pointe.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Trades_Qty_Good           ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_TradedAt_Id        ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Inhabitants_Planet_Credits ON dbo.Inhabitants;
DROP INDEX IF EXISTS IX_Lab_Species_Class_Life        ON dbo.Species;
DROP INDEX IF EXISTS IX_Lab_Markets_Planet_Date       ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Markets_Planet_Black      ON dbo.Markets;
DROP INDEX IF EXISTS IX_Lab_Planets_Population        ON dbo.Planets;

DROP PROCEDURE IF EXISTS dbo.Lab_PageEchanges;
DROP PROCEDURE IF EXISTS dbo.Lab_BiensParIds;
DROP TYPE      IF EXISTS dbo.Lab_IdList;
DROP TABLE     IF EXISTS dbo.Lab_Blob;

-- Si vous avez créé la base de test du modèle compilé / des migrations, supprimez-la séparément.

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
SELECT name FROM sys.types WHERE name LIKE 'Lab[_]%';
```
