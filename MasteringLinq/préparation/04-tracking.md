# 04 — Suivi des changements (*change tracking*) et lectures

> Suite de [`03-donnees-liees.md`](03-donnees-liees.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans). Pour le volume réseau : `MesurerReseau` du [03](03-donnees-liees.md).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> **Le tracking n'agit pas sur le SQL** : le texte envoyé est le même avec ou sans suivi. Les différences se lisent côté **.NET** (temps, allocations, GC, mémoire, nombre d'entités suivies). Les sections « Sans index / avec index » le rappellent et traitent les cas où un objet SQL (vue, index) a un sens (4.7).
> Index et objets SQL : préfixes `IX_Lab_` / `Lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

- [Outils pour observer le suivi](#outils-pour-observer-le-suivi)
- [4.1 `AsNoTracking`](#41-asnotracking)
- [4.2 `AsNoTrackingWithIdentityResolution`](#42-asnotrackingwithidentityresolution)
- [4.3 Le coût du suivi : instantanés, table d'identité, *fixup*, `DetectChanges`](#43-le-coût-du-suivi)
- [4.4 Réglage global : `UseQueryTrackingBehavior`](#44-réglage-global--usequerytrackingbehavior)
- [4.5 Traitements par lots : `AutoDetectChangesEnabled` et `Clear()`](#45-traitements-par-lots)
- [4.6 Une projection n'est pas suivie… sauf si elle contient une entité](#46-projection-et-suivi)
- [4.7 Entités sans clé, vues, `ToView`, `ToSqlQuery`](#47-entités-sans-clé-vues-toview-tosqlquery)
- [Script de nettoyage](#script-de-nettoyage)

## Outils pour observer le suivi

```csharp
// Nombre d'entités suivies
Console.WriteLine($"Entités suivies : {db.ChangeTracker.Entries().Count():N0}");

// Détail du suivi (états, valeurs courantes) — attention : volumineux au-delà de quelques centaines d'entités
Console.WriteLine(db.ChangeTracker.DebugView.ShortView);

// Pic de mémoire : voir l'échantillonneur de la boîte à outils du 00 (section D)
```
`dotnet-counters monitor --process-id <PID> System.Runtime` : `GC Heap Size`, `Gen 2 GC Count`.

---

## 4.1 `AsNoTracking`

**Axe concerné : ⚙️ .NET (CPU) + 🧠 mémoire. Aucun effet sur le SQL.**

### La notion

Par défaut, chaque entité lue est **suivie** : EF conserve une **référence** (table d'identité), un **instantané** des valeurs d'origine (pour détecter les modifications) et relie les navigations (*fixup*). Pour une lecture qui ne sera **pas** suivie d'un `SaveChanges`, `AsNoTracking()` évite tout cela : moins d'objets, moins d'allocations, aucun `DetectChanges`, résultat immédiatement libérable par le GC.

### Dans quels cas on est concerné

- Toute **lecture seule** : listes, rapports, exports, API GET.
- Gros volumes (centaines de milliers d'entités).
- Pas dans le cas où l'on modifiera ces entités puis appellera `SaveChanges`.

### Exemple sans l'amélioration

```csharp
// ❌ 1 M de biens lus avec suivi, juste pour les afficher/agréger
await Mesurer("AVANT : avec suivi", async db =>
{
    var l = await db.Goods.ToListAsync();
    return $"{l.Count:N0} biens, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Même requête, sans suivi
await Mesurer("APRÈS : AsNoTracking", async db =>
{
    var l = await db.Goods.AsNoTracking().ToListAsync();
    return $"{l.Count:N0} biens, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**À observer** : le SQL **identique** ; la ligne « résultat » : 1 000 000 entités suivies contre 0 ; « Mo alloués » et « GC » (en particulier `GC2`) ; le temps.

### Sans index / avec index

Sans objet : le SQL n'a pas changé. (Vérifiez-le avec `afficherSql: true` : même `SELECT`, même plan, mêmes *logical reads*.)

### Comparaison avec une procédure stockée

Sans objet : le suivi est une affaire de .NET. Une entité obtenue par `FromSql("EXEC …")` peut être suivie ou non (ajoutez `.AsNoTracking()`).

### 🧪 TP 4.1 — « Quand ne pas mettre `AsNoTracking` »

*Énoncé.* Écrivez deux méthodes : `ListerBiens()` (lecture seule) et `AugmenterPrix(int id, decimal pct)` (modification). Dites pour chacune si elle doit utiliser `AsNoTracking`, et montrez ce qui se passe si l'on met `AsNoTracking` dans la seconde (utilisez une **transaction annulée**).

<details><summary>Correction</summary>

```csharp
static Task<List<Good>> ListerBiens(StarWarsContext db) => db.Goods.AsNoTracking().Where(g => g.Id <= 1_000).ToListAsync();   // ✔ sans suivi

static async Task<int> AugmenterPrix(StarWarsContext db, int id, decimal pct)
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var g = await db.Goods.AsNoTracking().FirstAsync(x => x.Id == id);      // ❌ non suivi
    g.BasePrice *= 1 + pct;
    var n = await db.SaveChangesAsync();                                      // 0 : rien n'est suivi, rien n'est écrit
    await tx.RollbackAsync();
    return n;
}
```
Avec `AsNoTracking`, `SaveChanges` retourne `0` : **aucune erreur, aucun `UPDATE`**. C'est la raison pour laquelle on réserve `AsNoTracking` aux lectures et on s'appuie sur le suivi pour les écritures (ou sur `ExecuteUpdate`, voir chapitre 5).
</details>

---

## 4.2 `AsNoTrackingWithIdentityResolution`

**Axe concerné : 🧠 mémoire + ⚙️ .NET.**

### La notion

Avec `AsNoTracking()`, si 10 échanges référencent la **même** société via `Include(t => t.Company)`, EF crée **10 objets `Company` distincts** (une copie par ligne), puisqu'il n'a pas de table d'identité. `AsNoTrackingWithIdentityResolution()` garde une **table d'identité pour la durée de la requête** : une seule instance par clé, partagée par les 10 échanges, sans suivi persistant dans le contexte (les entités ne sont pas conservées après la requête).

### Dans quels cas on est concerné

- `Include` d'une navigation de **référence** partagée par de nombreux enfants (société d'un échange, planète d'un habitant).
- Graphes avec cycles (sérialisation).
- Pour comparer par référence (`a.Company == b.Company`).

### Exemple sans l'amélioration

```csharp
// ❌ AsNoTracking + Include : une copie de la société par échange
await Mesurer("AVANT : AsNoTracking + Include", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.CompanyId <= 20_000).Include(t => t.Company).ToListAsync();
    var distinctes = l.Select(t => t.Company).Distinct(ReferenceEqualityComparer.Instance).Count();
    return $"{l.Count:N0} échanges, {distinctes:N0} objets Company distincts";
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Une instance par société, sans suivi persistant
await Mesurer("APRÈS : AsNoTrackingWithIdentityResolution", async db =>
{
    var l = await db.Trades.AsNoTrackingWithIdentityResolution().Where(t => t.CompanyId <= 20_000).Include(t => t.Company).ToListAsync();
    var distinctes = l.Select(t => t.Company).Distinct(ReferenceEqualityComparer.Instance).Count();
    return $"{l.Count:N0} échanges, {distinctes:N0} objets Company distincts, suivies : {db.ChangeTracker.Entries().Count()}";
});

// Pour comparaison : avec suivi (une instance par société, mais conservées dans le contexte)
await Mesurer("Avec suivi", async db =>
{
    var l = await db.Trades.Where(t => t.CompanyId <= 20_000).Include(t => t.Company).ToListAsync();
    return $"{l.Count:N0} échanges, suivies : {db.ChangeTracker.Entries().Count():N0}";
});
```
**À observer** : « objets Company distincts » ≈ 120 000 contre ≈ 20 000 ; mémoire et allocations ; le SQL identique (le `JOIN` répète les colonnes de la société sur chaque ligne dans les trois cas : **c'est le transfert, pas l'objet**, que seule la projection (4.6) supprime).

### Sans index / avec index et procédure stockée

Sans objet : même SQL.

### 🧪 TP 4.2 — « Combien d'instances ? »

*Énoncé.* Chargez les 120 000 échanges des 20 000 premières sociétés avec leur société, dans les trois modes (tracking, no-tracking, identity resolution). Remplissez : instances `Company` distinctes / entités suivies / Mo alloués / temps. Quel mode pour un export JSON ? Pourquoi ?

<details><summary>Correction</summary>

Attendu : *tracking* = 20 000 instances, 140 000 entités suivies ; *no-tracking* = 120 000 instances, 0 suivi ; *identity resolution* = 20 000 instances, 0 suivi. Pour un export JSON de graphe, `AsNoTrackingWithIdentityResolution` évite de dupliquer les objets et gère les références partagées sans conserver de suivi. Pour un export à plat, la **projection** (4.6) reste préférable.
</details>

---

## 4.3 Le coût du suivi

**Axe concerné : ⚙️ .NET (CPU, `DetectChanges`) + 🧠 mémoire.**

### La notion

Suivre une entité coûte :
1. un **instantané** de ses valeurs (mémoire) ;
2. une entrée dans la **table d'identité** (recherche par clé) ;
3. le *fixup* des navigations ;
4. à chaque `SaveChanges` (et à certains appels : `Entries()`, `Find`, `Local`…), un **`DetectChanges`** qui compare **toutes** les entités suivies à leur instantané : coût **proportionnel au nombre d'entités suivies**, même pour modifier une seule ligne.

### Dans quels cas on est concerné

- Contexte long (un contexte « par session ») qui accumule des entités.
- Traitement par lot avec milliers d'entités suivies et `SaveChanges` répétés.
- Modifier une ligne en en chargeant beaucoup d'autres.

### Exemple sans l'amélioration

```csharp
// ❌ Beaucoup d'entités suivies : DetectChanges devient cher
await Mesurer("AVANT : DetectChanges avec 200 000 entités", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 200_000).ToListAsync();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < 10; i++) db.ChangeTracker.DetectChanges();
    return $"{l.Count:N0} entités ; 10 × DetectChanges = {sw.ElapsedMilliseconds} ms";
});

// ❌ Charger une entité complète pour modifier une seule colonne (transaction annulée)
await Mesurer("AVANT : lire puis modifier", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var g = await db.Goods.FirstAsync(x => x.Id == 42);
    g.BasePrice += 1;
    var n = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return n;
}, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Moins d'entités suivies : DetectChanges proportionnel
await Mesurer("APRÈS : DetectChanges avec 2 000 entités", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 2_000).ToListAsync();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < 10; i++) db.ChangeTracker.DetectChanges();
    return $"{l.Count:N0} entités ; 10 × DetectChanges = {sw.ElapsedMilliseconds} ms";
});

// ✔ Modifier sans lire : on « attache » une entité partielle et on marque la seule colonne modifiée
await Mesurer("APRÈS : Attach + IsModified", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var g = new Good { Id = 42, Sku = "", Name = "", BasePrice = 124.45m };
    db.Attach(g);
    db.Entry(g).Property(x => x.BasePrice).IsModified = true;
    var n = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return n;
}, afficherSql: true);

// ✔ Ou ExecuteUpdate (chapitre 5) : aucune entité, aucun suivi
await Mesurer("APRÈS : ExecuteUpdate", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var n = await db.Goods.Where(g => g.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice + 1));
    await tx.RollbackAsync();
    return n;
}, afficherSql: true);
```
**À observer** : le temps de `DetectChanges` (×100 entre 2 000 et 200 000 entités environ) ; dans le SQL de `Attach + IsModified` : `UPDATE … SET [BasePrice] = @p0 WHERE [Id] = @p1` — **une seule colonne** — contre une lecture préalable de la ligne pour le premier cas.

### Sans index / avec index

Même remarque : le suivi n'agit pas sur le SQL. Le seul SQL en jeu (`UPDATE … WHERE Id = 42`) utilise la clé primaire.

### Comparaison avec une procédure stockée

Un `UPDATE` dans une procédure `Lab_AugmenterPrix @Id, @Delta` est l'équivalent du `ExecuteUpdate`. Même coût SQL ; la procédure n'a pas à matérialiser d'entité. Comparez, c'est l'occasion de constater que **les trois voies (Attach, ExecuteUpdate, procédure) envoient quasiment le même `UPDATE`**.

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_AugmenterPrix @Id int, @Delta decimal(18,2)
AS BEGIN SET NOCOUNT ON; UPDATE dbo.Goods SET BasePrice = BasePrice + @Delta WHERE Id = @Id; END
```
```csharp
await Mesurer("PROC : UPDATE", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var n = await db.Database.ExecuteSqlAsync($"EXEC dbo.Lab_AugmenterPrix {42}, {1m}");
    await tx.RollbackAsync();
    return n;
});
```

### 🧪 TP 4.3 — « Mesurer le coût de `DetectChanges` »

*Énoncé.* Mesurez `DetectChanges()` avec 1 000, 10 000, 100 000 et 500 000 entités suivies. Tracez le temps en fonction du nombre d'entités et dites si la croissance est linéaire. Que recommandez-vous pour un traitement qui modifie 10 lignes parmi 500 000 chargées ?

<details><summary>Correction</summary>

Méthode : pour chaque taille, charger `Goods` d'`Id ≤ N` avec suivi, mesurer 10 appels de `DetectChanges`. La croissance est **linéaire** (chaque entité est comparée à son instantané). Recommandation : ne pas charger 500 000 entités suivies pour en modifier 10 — filtrer les lignes à modifier, ou utiliser `ExecuteUpdate`, ou `Attach + IsModified`, ou désactiver temporairement `AutoDetectChangesEnabled` (4.5).
</details>

---

## 4.4 Réglage global : `UseQueryTrackingBehavior`

**Axe concerné : ⚙️ .NET + sécurité de l'application.**

### La notion

`UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)` rend **toutes** les requêtes non suivies par défaut ; `AsTracking()` réactive le suivi pour les requêtes qui en ont besoin. Avantage : on ne risque plus d'oublier `AsNoTracking`. Piège : le code qui **lit puis modifie** sans `AsTracking()` n'écrit **rien**, **sans erreur** (voir 4.1).

### Dans quels cas on est concerné

- Applications surtout **en lecture** (API de consultation, reporting).
- Équipes où l'oubli de `AsNoTracking` est fréquent.

### Exemple sans l'amélioration

```csharp
// ❌ Suivi par défaut, appliqué à tout (y compris les lectures seules)
await Mesurer("AVANT : défaut (suivi)", async db => (await db.Companies.ToListAsync()).Count);
```

### Exemple avec l'amélioration

Dans `NewDb` ajoutez `.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)` (ou créez `NewDbNoTrack()`).

```csharp
// ✔ Même code, mais sans suivi par défaut
await Mesurer("APRÈS : défaut sans suivi", async db => (await db.Companies.ToListAsync()).Count);

// ✔ Écriture : réactiver le suivi explicitement
await Mesurer("AsTracking ciblé", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var g = await db.Goods.AsTracking().FirstAsync(x => x.Id == 42);
    g.BasePrice += 1;
    var n = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return n;
});

// ⚠ Le piège
await Mesurer("PIÈGE : sans AsTracking", async db =>
{
    var g = await db.Goods.FirstAsync(x => x.Id == 42);
    g.BasePrice += 1;
    return await db.SaveChangesAsync();                 // 0 : aucune écriture, aucune erreur
});
```
**À observer** : `0` renvoyé par le piège ; la baisse d'allocations des lectures ; le nombre d'entités suivies.

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 4.4 — « Sécuriser le défaut »

*Énoncé.* Vous basculez une application en `NoTracking` par défaut. Écrivez un test automatisé qui détecte le piège « modification sans `AsTracking` » : il doit échouer si `SaveChanges` retourne 0 alors qu'une modification était attendue.

<details><summary>Correction</summary>

```csharp
static async Task<int> SauverOuEchouer(StarWarsContext db)
{
    var attendu = db.ChangeTracker.Entries().Count(e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted);
    var n = await db.SaveChangesAsync();
    if (attendu == 0) throw new InvalidOperationException("Aucune entité modifiée n'est suivie : AsTracking() manquant ?");
    return n;
}
```
Idée générale : en `NoTracking` par défaut, **tout code d'écriture** doit passer par un point unique qui vérifie qu'il y avait bien des entités suivies à écrire. Une autre option : séparer deux types de contexte (lecture / écriture).
</details>

---

## 4.5 Traitements par lots

**Axe concerné : ⚙️ .NET (CPU) + 🧠 mémoire — `SaveChanges` en masse.**

### La notion

Lors d'un traitement de **dizaines de milliers d'entités** suivies :
- **`AutoDetectChangesEnabled = false`** : EF cesse d'appeler `DetectChanges` automatiquement (avant `Find`, `Entries`, `SaveChanges`…). À manier avec précaution : vous devez appeler `DetectChanges()` à la main avant `SaveChanges` si des entités **déjà suivies** ont été modifiées.
- **`ChangeTracker.Clear()`** entre deux lots : libère les entités suivies.
- Traiter par **lots de 1 000 à 10 000** : `SaveChanges` puis `Clear()`.

### Dans quels cas on est concerné

- Imports, migrations de données, recalculs sur de grands ensembles d'entités.
- Boucles qui appellent `Find`/`Entries`/`Local` sur un contexte qui grossit (cout quadratique).

### Mise en place (table de démonstration)

```sql
CREATE TABLE dbo.Lab_Item (Id int IDENTITY(1,1) PRIMARY KEY, Nom nvarchar(30) NOT NULL, Valeur int NOT NULL);
```
```csharp
public class LabItem { public int Id { get; set; } public string Nom { get; set; } = ""; public int Valeur { get; set; } }
// Dans votre classe partielle du contexte :
//   public DbSet<LabItem> LabItems => Set<LabItem>();
//   et dans OnModelCreatingPartial : modelBuilder.Entity<LabItem>().ToTable("Lab_Item");
```

### Exemple sans l'amélioration

```csharp
// ❌ 50 000 entités suivies avant un unique SaveChanges
await Mesurer("AVANT : 1 SaveChanges, tout suivi", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    for (var i = 0; i < 50_000; i++) db.LabItems.Add(new LabItem { Nom = $"x{i}", Valeur = i });
    var n = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return $"{n:N0} lignes ; suivies avant Clear : {db.ChangeTracker.Entries().Count():N0}";
});

// ❌ Find dans une boucle avec un contexte qui grossit : DetectChanges à chaque appel
await Mesurer("AVANT : Find en boucle (20 000)", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 20_000).ToListAsync();
    var n = 0;
    foreach (var id in Enumerable.Range(1, 20_000)) if (await db.Goods.FindAsync(id) is not null) n++;
    return n;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Lots + Clear
await Mesurer("APRÈS : lots de 5 000 + Clear()", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var total = 0;
    for (var lot = 0; lot < 10; lot++)
    {
        for (var i = 0; i < 5_000; i++) db.LabItems.Add(new LabItem { Nom = $"x{lot}-{i}", Valeur = i });
        total += await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
    await tx.RollbackAsync();
    return $"{total:N0} lignes ; suivies à la fin : {db.ChangeTracker.Entries().Count()}";
});

// ✔ AutoDetectChanges désactivé pendant l'ajout, rétabli ensuite
await Mesurer("APRÈS : AutoDetectChanges = false", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    db.ChangeTracker.AutoDetectChangesEnabled = false;
    for (var i = 0; i < 50_000; i++) db.LabItems.Add(new LabItem { Nom = $"x{i}", Valeur = i });
    db.ChangeTracker.DetectChanges();                       // une seule fois
    var n = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return n;
});
```
**À observer** : le temps et la mémoire (pic) ; le nombre de lots ; dans le SQL, le **batching** de `INSERT` (`MERGE`/`INSERT … VALUES` multiples) ; le temps du `Find` en boucle (quadratique) comparé à un seul `Where(ids.Contains(...))` chargé une fois.

### Sans index / avec index

Les `INSERT` dans `Lab_Item` utilisent la clé `IDENTITY` ; chaque index supplémentaire sur la table **ralentirait** l'insertion (compromis lecture/écriture à garder en tête).

### Comparaison avec une procédure stockée

Un `INSERT … SELECT` ensembliste dans une procédure (ou `SqlBulkCopy` côté client) est de loin le plus rapide pour de très gros volumes ; voir le chapitre 5.

### 🧪 TP 4.5 — « 50 000 lignes sans exploser »

*Énoncé.* Insérez 50 000 `LabItem` : (a) un seul `SaveChanges`, (b) lots de 5 000 avec `Clear()`, (c) `AutoDetectChangesEnabled = false`. Tableau : temps / pic mémoire / entités suivies à la fin. Que choisissez-vous pour 5 M de lignes ?

<details><summary>Correction</summary>

(a) mémoire et `DetectChanges` proportionnels à 50 000 ; (b) mémoire **bornée** par la taille du lot ; (c) supprime les `DetectChanges` répétés mais ne réduit pas la mémoire. Pour 5 M de lignes : (b)+(c) combinés (lots, pas de détection automatique, `Clear()`), ou mieux, `SqlBulkCopy` (chapitre 5). Dans tous les cas, **pensez à vider la table de démonstration** si vous n'annulez pas la transaction.
</details>

---

## 4.6 Projection et suivi

**Axe concerné : ⚙️ .NET + 🧠 mémoire + 🗄️ SQL (colonnes).**

### La notion

Une projection vers un type **anonyme ou DTO** ne produit **aucune entité** : rien n'est suivi. **Mais** si la projection **contient une entité** (`new { t, Total = … }`, `Select(t => t)` dans un type anonyme), cette entité **est suivie** (sauf `AsNoTracking`). Moralité : une projection est « sans suivi » **par construction**, tant qu'elle ne rapporte pas d'entité.

### Dans quels cas on est concerné

- Optimiser des lectures en passant par des DTO.
- Types anonymes contenant l'entité par commodité.

### Exemple sans l'amélioration

```csharp
// ❌ Une entité glissée dans la projection → 200 000 entités suivies
await Mesurer("AVANT : entité dans la projection", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 200_000).Select(g => new { Bien = g, Taxe = g.BasePrice * 0.2m }).ToListAsync();
    return $"{l.Count:N0} lignes ; suivies : {db.ChangeTracker.Entries().Count():N0}";
}, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Uniquement les colonnes utiles
await Mesurer("APRÈS : colonnes seules", async db =>
{
    var l = await db.Goods.Where(g => g.Id <= 200_000).Select(g => new { g.Id, g.Sku, Taxe = g.BasePrice * 0.2m }).ToListAsync();
    return $"{l.Count:N0} lignes ; suivies : {db.ChangeTracker.Entries().Count():N0}";
}, afficherSql: true);

// ✔ Ou la même chose avec un DTO nominatif (déclarer le record EN BAS de Program.cs, après les instructions)
public record BienDto(int Id, string Sku, decimal Taxe);
await Mesurer("APRÈS : DTO", async db =>
    (await db.Goods.Where(g => g.Id <= 200_000).Select(g => new BienDto(g.Id, g.Sku, g.BasePrice * 0.2m)).ToListAsync()).Count);
```
**À observer** : « suivies » (200 000 contre 0) ; la liste de colonnes du `SELECT` (8 contre 2) ; les allocations.

### Sans index / avec index

Même idée qu'au chapitre 1 : une projection ouvre la porte à un **index couvrant** (voir 1.1) ; l'entité complète non.

### Comparaison avec une procédure stockée

Une procédure renvoyant exactement ces colonnes se mappe sur un DTO via `SqlQuery<T>` (jamais suivi) — même résultat qu'une projection LINQ.

### 🧪 TP 4.6 — « Suivi caché »

*Énoncé.* Réécrivez `Select(g => new { g, Prix = g.BasePrice })` pour qu'**aucune** entité ne soit suivie, de trois façons : `AsNoTracking`, projection de colonnes, DTO. Vérifiez avec `ChangeTracker.Entries().Count()`.

<details><summary>Correction</summary>

(1) `db.Goods.AsNoTracking().Select(g => new { g, Prix = g.BasePrice })` : l'entité est matérialisée mais pas suivie ; (2) `Select(g => new { g.Id, g.Sku, Prix = g.BasePrice })` : plus d'entité ; (3) `Select(g => new BienDto(...))`. Les options (2) et (3) lisent moins de colonnes ; (1) conserve toutes les colonnes.
</details>

---

## 4.7 Entités sans clé, vues, `ToView`, `ToSqlQuery`

**Axe concerné : 🗄️ SQL (calcul dans la base, vue indexée) + ⚙️ .NET (jamais de suivi).**

### La notion

Une **entité sans clé** (`HasNoKey()`) n'est **jamais suivie** : elle représente un résultat de lecture (vue, requête, procédure). On la mappe sur :
- une **vue** SQL (`ToView("nom")`) ;
- une **requête** SQL (`ToSqlQuery("SELECT …")`) ;
- le résultat d'une procédure (`Database.SqlQuery<T>`, sans déclaration au modèle).

Une **vue indexée** (matérialisée) précalcule des agrégats : lecture quasi instantanée, au prix d'une **maintenance à chaque écriture** sur les tables sources.

### Dans quels cas on est concerné

- Rapports et agrégats récurrents.
- Réutilisation d'une requête SQL complexe comme source LINQ composable.
- Données qui ne se modifient jamais depuis l'application (lecture seule).

### Mise en place

```sql
CREATE OR ALTER VIEW dbo.Lab_vwVentesParBien AS
SELECT GoodId, COUNT_BIG(*) AS Echanges, SUM(CAST(Quantity AS bigint)) AS QuantiteTotale
FROM dbo.Trades GROUP BY GoodId;
```
```csharp
public class VentesParBien { public int GoodId { get; set; } public long Echanges { get; set; } public long QuantiteTotale { get; set; } }
// Dans OnModelCreatingPartial :
//   modelBuilder.Entity<VentesParBien>().HasNoKey().ToView("Lab_vwVentesParBien");
//   et dans le contexte : public DbSet<VentesParBien> VentesParBien => Set<VentesParBien>();
```

### Exemple sans l'amélioration

```csharp
// ❌ Agrégat calculé à chaque appel sur 6 M de lignes
await Mesurer("AVANT : GroupBy à la volée", async db =>
    (await db.Trades.GroupBy(t => t.GoodId).Select(g => new { g.Key, N = g.Count(), Q = g.Sum(t => (long)t.Quantity) })
        .OrderByDescending(x => x.Q).Take(10).ToListAsync()).Count, afficherSql: true);

```

### Exemple avec l'amélioration

```csharp
// ✔ Vue non indexée : même coût SQL, mais la requête est nommée, réutilisable et composable
await Mesurer("APRÈS (1) : vue", async db =>
    (await db.VentesParBien.OrderByDescending(v => v.QuantiteTotale).Take(10).ToListAsync()).Count, afficherSql: true);
```
```sql
-- ✔ Vue indexée : le résultat est stocké (et maintenu) par SQL Server
CREATE OR ALTER VIEW dbo.Lab_vwVentesParBienIdx WITH SCHEMABINDING AS
SELECT GoodId, COUNT_BIG(*) AS Echanges, SUM(CAST(Quantity AS bigint)) AS QuantiteTotale
FROM dbo.Trades GROUP BY GoodId;
GO
CREATE UNIQUE CLUSTERED INDEX IX_Lab_vwVentesParBienIdx ON dbo.Lab_vwVentesParBienIdx (GoodId);
```
```csharp
// Mappez de la même façon un type VentesParBienIdx sur ToView("Lab_vwVentesParBienIdx") et mesurez :
await Mesurer("APRÈS (2) : vue indexée", async db =>
    (await db.VentesParBienIdx.OrderByDescending(v => v.QuantiteTotale).Take(10).ToListAsync()).Count, afficherSql: true);
```
**À observer** : `SET STATISTICS IO` — la vue non indexée relit les 6 M de lignes de `Trades` ; la vue indexée lit **la vue elle-même** (1 M de lignes max, pas de recalcul). Éditions : l'optimiseur n'utilise une vue indexée « automatiquement » que dans certaines éditions (l'édition Developer est équivalente à Enterprise) ; sinon il faut `WITH (NOEXPAND)`. **Coût caché** : chaque `INSERT/UPDATE/DELETE` sur `Trades` doit maintenant mettre à jour la vue (mesurez une insertion avec et sans la vue, dans une transaction annulée).

### Sans index / avec index

C'est précisément la comparaison : sans index, la vue est une simple macro ; **avec** l'index clusterisé, elle devient une table matérialisée.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_Top10Biens
AS BEGIN SET NOCOUNT ON;
  SELECT TOP (10) GoodId, COUNT_BIG(*) AS Echanges, SUM(CAST(Quantity AS bigint)) AS QuantiteTotale
  FROM dbo.Trades GROUP BY GoodId ORDER BY SUM(CAST(Quantity AS bigint)) DESC; END
```
```csharp
await Mesurer("PROC : Top10 biens", async db =>
    (await db.Database.SqlQuery<VentesParBien>($"EXEC dbo.Lab_Top10Biens").ToListAsync()).Count);
```
**À observer** : même plan que le `GroupBy` LINQ (calcul à chaque appel). La **vue indexée** bat les deux sur la lecture ; la procédure ne change rien à ce niveau.

### 🧪 TP 4.7 — « Lecture rapide, écriture lente »

*Énoncé.* Mesurez (a) la lecture du top 10 sur la vue simple et sur la vue indexée ; (b) l'insertion de 10 000 lignes dans `Trades` **dans une transaction annulée**, avec et sans la vue indexée. Concluez sur la pertinence d'une vue indexée pour une table très écrite.

<details><summary>Correction</summary>

(a) la vue indexée lit quelques pages (colonne `GoodId` clusterisée) au lieu de scanner `Trades` ; (b) avec la vue indexée, chaque `INSERT` met à jour la ligne agrégée correspondante : l'insertion est plus lente et crée de la contention sur les lignes d'agrégat populaires. Une vue indexée convient aux tables **peu écrites et très lues** ; pour une table très écrite, on préfère une table de pré-agrégation alimentée par lots ou une vue non indexée avec cache applicatif.
</details>

---

## Script de nettoyage

```sql
DROP VIEW      IF EXISTS dbo.Lab_vwVentesParBienIdx;      -- supprime aussi l'index clusterisé de la vue
DROP VIEW      IF EXISTS dbo.Lab_vwVentesParBien;
DROP PROCEDURE IF EXISTS dbo.Lab_AugmenterPrix;
DROP PROCEDURE IF EXISTS dbo.Lab_Top10Biens;
DROP TABLE     IF EXISTS dbo.Lab_Item;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
-- Données non modifiées (toutes les écritures étaient dans des transactions annulées) :
SELECT BasePrice FROM dbo.Goods WHERE Id = 42;   -- à comparer avec sa valeur initiale
```
