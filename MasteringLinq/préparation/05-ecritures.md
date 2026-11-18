# 05 — Écritures et opérations de masse

> Suite de [`04-tracking.md`](04-tracking.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**. Les points dépendant de la version d'EF Core sont marqués *(à vérifier)*.
> **Règle de sécurité du chapitre : on n'altère jamais durablement les tables du jeu de données.** Toute écriture sur `Goods`, `Trades`… se fait **dans une transaction annulée** (`BeginTransactionAsync` … `RollbackAsync`) ; les insertions de masse ciblent une table de démonstration `Lab_Item`. Le **script de nettoyage** est en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

- [Mise en place](#mise-en-place)
- [5.1 `ExecuteUpdate` / `ExecuteDelete`](#51-executeupdate--executedelete)
- [5.2 `SetProperty` : valeurs calculées et dynamiques](#52-setproperty--valeurs-calculées-et-dynamiques)
- [5.3 Les limites d'`ExecuteUpdate`](#53-les-limites-dexecuteupdate)
- [5.4 Le *batching* de `SaveChanges`](#54-le-batching-de-savechanges)
- [5.5 Insertions massives : `AddRange` vs `SqlBulkCopy`](#55-insertions-massives)
- [5.6 `SqlQuery<T>` et `ExecuteSql`](#56-sqlqueryt-et-executesql)
- [5.7 `FromSql` composable](#57-fromsql-composable)
- [5.8 Migrations *bundle*](#58-migrations-bundle)
- [5.9 Concurrence optimiste](#59-concurrence-optimiste)
- [5.10 `ExecutionStrategy` et transactions](#510-executionstrategy-et-transactions)
- [Script de nettoyage](#script-de-nettoyage)

## Mise en place

Table de démonstration (déjà décrite au chapitre 04 ; ré-exécutable sans risque — **ne déclarez la classe `LabItem` qu'une seule fois** dans votre projet si vous avez déjà suivi le 04) :

```sql
IF OBJECT_ID('dbo.Lab_Item') IS NULL
    CREATE TABLE dbo.Lab_Item (Id int IDENTITY(1,1) PRIMARY KEY, Nom nvarchar(30) NOT NULL, Valeur int NOT NULL);
```
```csharp
public class LabItem { public int Id { get; set; } public string Nom { get; set; } = ""; public int Valeur { get; set; } }
// Classe partielle du contexte :  public DbSet<LabItem> LabItems => Set<LabItem>();
// OnModelCreatingPartial        :  modelBuilder.Entity<LabItem>().ToTable("Lab_Item");
```
`GENERATE_SERIES` (utilisé plus bas) demande SQL Server 2022 et un niveau de compatibilité 160 : `SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();`. `NewDbAvecRetry()` (5.10) est une variante de `NewDb` avec `.UseSqlServer(Cs, o => o.EnableRetryOnFailure())`.

Modèle de mesure pour **toute écriture sur les tables d'origine** (à réutiliser) :

```csharp
async Task<T> DansTransactionAnnulee<T>(StarWarsContext db, Func<Task<T>> action)
{
    await using var tx = await db.Database.BeginTransactionAsync();
    try { return await action(); }
    finally { await tx.RollbackAsync(); }          // le coût du rollback n'est PAS dans le temps mesuré par Mesurer
}
```
> Le **ROLLBACK** d'une grosse modification est lui-même coûteux (SQL Server annule les écritures du journal). Il n'apparaît pas dans le temps mesuré si vous le placez après l'arrêt du chronomètre ; sur un `COMMIT` réel, le coût d'écriture du journal fait partie du coût de la requête.

---

## 5.1 `ExecuteUpdate` / `ExecuteDelete`

**Axe concerné : 🗄️ SQL (une instruction ensembliste) + ⚙️ .NET (aucune entité) + 🧠 mémoire.**

### La notion

`ExecuteUpdate` / `ExecuteDelete` traduisent un LINQ **directement en `UPDATE` / `DELETE` ensembliste**, **sans charger** les lignes dans .NET et **sans suivi**. Le coût devient celui de l'instruction SQL elle-même (verrous, journal, index à maintenir) au lieu de : lecture → matérialisation → détection de changements → un `UPDATE` par ligne (en lots).

### Dans quels cas on est concerné

- Modification ou suppression **en masse** par un critère (prix de tous les biens de contrebande, purge d'historique).
- Toute boucle `foreach (var x in await q.ToListAsync()) { x.Prop = …; } await SaveChangesAsync();`.

### Exemple sans l'amélioration

```csharp
// ❌ Lire ~80 000 biens de contrebande, les suivre, les modifier un par un
await Mesurer("AVANT : ToList + SaveChanges", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var l = await db.Goods.Where(g => g.IsContraband).ToListAsync();
        foreach (var g in l) g.BasePrice *= 1.10m;
        var n = await db.SaveChangesAsync();
        return $"{n:N0} lignes modifiées ; suivies : {db.ChangeTracker.Entries().Count():N0}";
    }), afficherSql: false);
```

### Exemple avec l'amélioration

```csharp
// ✔ Une instruction UPDATE
await Mesurer("APRÈS : ExecuteUpdate", async db =>
    await DansTransactionAnnulee(db, async () =>
        $"{await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.10m)):N0} lignes modifiées"),
    afficherSql: true);

// ✔ Suppression ensembliste (table de démonstration, après y avoir inséré des lignes)
await Mesurer("APRÈS : ExecuteDelete", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        await db.Database.ExecuteSqlAsync($"INSERT INTO dbo.Lab_Item (Nom, Valeur) SELECT CONCAT(N'x', value), value FROM GENERATE_SERIES(1, 100000)");
        return $"{await db.LabItems.Where(i => i.Valeur % 2 == 0).ExecuteDeleteAsync():N0} lignes supprimées";
    }), afficherSql: true);
```
**À observer** : un **seul** `UPDATE [g] SET [g].[BasePrice] = [g].[BasePrice] * 1.10 FROM [Goods] AS [g] WHERE [g].[IsContraband] = CAST(1 AS bit)` ; pour l'« avant », des **dizaines de milliers** d'`UPDATE` regroupés par lots (voir 5.4) ; les entités suivies (≈ 80 000 contre 0) ; mémoire et temps.

### Sans index / avec index

Deux effets **opposés** à mesurer (`SET STATISTICS IO, TIME ON`, plan) :

```sql
-- (1) Retrouver les lignes à modifier : sans index, scan des 1 M de biens
BEGIN TRAN;
UPDATE dbo.Goods SET BasePrice = BasePrice * 1.10 WHERE IsContraband = 1;           -- scan clustered
ROLLBACK;
GO
CREATE INDEX IX_Lab_Goods_Contra ON dbo.Goods (Id) WHERE IsContraband = 1;           -- index filtré (~8 % des lignes)
GO
BEGIN TRAN;
UPDATE dbo.Goods SET BasePrice = BasePrice * 1.10 WHERE IsContraband = 1;           -- recherche par l'index filtré
ROLLBACK;
GO
-- (2) Chaque index qui contient la colonne modifiée doit être mis à jour : l'écriture ralentit
CREATE INDEX IX_Lab_Goods_BasePrice ON dbo.Goods (BasePrice);
BEGIN TRAN;
UPDATE dbo.Goods SET BasePrice = BasePrice * 1.10 WHERE IsContraband = 1;           -- le plan contient une opération « Index Update » en plus
ROLLBACK;
GO
DROP INDEX IX_Lab_Goods_Contra ON dbo.Goods; DROP INDEX IX_Lab_Goods_BasePrice ON dbo.Goods;
```
**Leçon** : un index **aide à trouver** les lignes, mais **coûte à maintenir** : chaque colonne indexée modifiée alourdit l'écriture (et le journal). Comparez le nombre de *logical reads/writes* dans les trois cas.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_MajPrixContrebande @Pct decimal(5,2)
AS BEGIN SET NOCOUNT ON; UPDATE dbo.Goods SET BasePrice = BasePrice * (1 + @Pct / 100) WHERE IsContraband = 1; SELECT @@ROWCOUNT AS Valeur; END
```
```csharp
await Mesurer("PROC : mise à jour", async db =>
    await DansTransactionAnnulee(db, async () => (await db.Database.SqlQuery<int>($"EXEC dbo.Lab_MajPrixContrebande {10m}").ToListAsync()).Single()));
```
**À observer** : même plan, mêmes écritures que `ExecuteUpdate` ; les deux écrasent l'« avant ». La procédure se justifie pour la **sécurité d'accès** (droits sur l'objet plutôt que sur la table) et la logique multi-étapes.

### 🧪 TP 5.1 — « Soldes de contrebande »

*Énoncé.* Baissez de 20 % le prix des biens de contrebande **périssables** dont le prix dépasse 50 000. Écrivez (a) la version « lire / modifier / sauver », (b) `ExecuteUpdate`. Comparez les requêtes émises, les entités suivies, les *logical reads*. Puis ajoutez l'index filtré et dites ce qui change.

<details><summary>Correction</summary>

```csharp
// (b)
await db.Goods.Where(g => g.IsContraband && g.IsPerishable && g.BasePrice > 50_000)
    .ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 0.8m));
```
(a) lit toutes les lignes concernées, les suit, puis envoie leurs `UPDATE` ligne par ligne (par lots) ; (b) envoie un seul `UPDATE`. L'index filtré (`WHERE IsContraband = 1`) réduit la recherche, mais **n'inclut pas** `IsPerishable` ni `BasePrice` : le plan fait un lookup pour les évaluer ; un index `(BasePrice) WHERE IsContraband = 1 AND IsPerishable = 1` conviendrait mieux, mais alourdirait l'écriture sur `BasePrice` — arbitrage à mesurer.
</details>

---

## 5.2 `SetProperty` : valeurs calculées et dynamiques

**Axe concerné : 🗄️ SQL (une passe pour plusieurs colonnes).**

### La notion

Dans `ExecuteUpdate`, `SetProperty(propriété, valeur)` accepte une **expression** utilisant les colonnes de la ligne (`g.BasePrice * 1.1m`, `g.WeightKg + 1`) ou une valeur constante/paramètre. On enchaîne **plusieurs** `SetProperty` pour **plusieurs colonnes dans la même instruction**. Dans les versions récentes *(à vérifier : EF 10)*, le paramètre de `ExecuteUpdate` peut être une **lambda non-expression** qui construit les `SetProperty` de façon **conditionnelle** (`if (…) s.SetProperty(…)`).

### Dans quels cas on est concerné

- Mise à jour de plusieurs colonnes selon une même condition.
- Mises à jour partielles pilotées par des paramètres (modifier le prix seulement si fourni).

### Exemple sans l'amélioration

```csharp
// ❌ Deux instructions UPDATE, deux parcours de la même table
await Mesurer("AVANT : 2 ExecuteUpdate", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var a = await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.1m));
        var b = await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));
        return a + b;
    }), afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Une seule instruction, deux colonnes
await Mesurer("APRÈS : 2 SetProperty", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Goods.Where(g => g.IsContraband)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.1m)
                                      .SetProperty(g => g.WeightKg, g => g.WeightKg + 1))), afficherSql: true);

// ✔ (EF 10, à vérifier) Setters conditionnels
decimal? nouveauPoids = null; decimal? facteurPrix = 1.1m;
await Mesurer("APRÈS : setters dynamiques", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s =>
        {
            if (facteurPrix is not null) s.SetProperty(g => g.BasePrice, g => g.BasePrice * facteurPrix.Value);
            if (nouveauPoids is not null) s.SetProperty(g => g.WeightKg, nouveauPoids.Value);
        })), afficherSql: true);
```
**À observer** : 2 `UPDATE` (2 scans) contre 1 (`SET [BasePrice] = …, [WeightKg] = …`) ; *logical reads* divisés par 2 ; le SQL du cas dynamique ne contient **que** les colonnes demandées.

### Sans index / avec index

Même remarque qu'en 5.1 : chaque colonne indexée modifiée est un coût d'écriture supplémentaire ; modifier **deux colonnes indexées** dans une seule instruction maintient chaque index **une fois par ligne**.

### Comparaison avec une procédure stockée

Équivalent direct (`UPDATE … SET a = …, b = …`). Aucune différence de plan.

### 🧪 TP 5.2 — « La mise à jour partielle »

*Énoncé.* Écrivez `AjusterBiens(db, decimal? facteurPrix, decimal? deltaPoids)` qui n'écrit **que** ce qui est fourni, pour les biens de contrebande. Vérifiez le SQL pour les trois combinaisons (prix seul, poids seul, les deux).

<details><summary>Correction</summary>

```csharp
static Task<int> AjusterBiens(StarWarsContext db, decimal? facteurPrix, decimal? deltaPoids) =>
    db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s =>
    {
        if (facteurPrix is { } f) s.SetProperty(g => g.BasePrice, g => g.BasePrice * f);
        if (deltaPoids is { } d)  s.SetProperty(g => g.WeightKg, g => g.WeightKg + d);
    });
```
Si votre version n'accepte que des expressions, alternative : une `if/else` qui choisit parmi trois `ExecuteUpdate` prédéfinis, ou une expression avec `ISNULL` (qui réécrit la colonne avec elle-même quand le paramètre est nul, au prix d'une écriture inutile).
</details>

---

## 5.3 Les limites d'`ExecuteUpdate`

**Axe concerné : justesse (cohérence du contexte, audit, concurrence) — la contrepartie de la vitesse.**

### La notion

`ExecuteUpdate`/`ExecuteDelete` **contournent** le `DbContext` :
1. **Le suivi n'est pas synchronisé** : une entité déjà chargée garde **l'ancienne valeur** en mémoire.
2. Les **`SaveChangesInterceptor`**, les événements `SavingChanges`, la logique d'**audit** écrite dans `SaveChanges` **ne s'exécutent pas**.
3. Les **jetons de concurrence** (`[Timestamp]`, `IsConcurrencyToken`) ne sont **pas vérifiés** automatiquement.
4. Les **suppressions en cascade** gérées côté client (`ClientCascade`) **n'ont pas lieu** : seules les cascades **de la base** et les contraintes agissent (une contrainte `NO ACTION` fait **échouer** la suppression).

### Dans quels cas on est concerné

- Du code qui mélange chargement d'entités et mises à jour de masse dans le même contexte.
- Applications avec audit/journalisation dans `SaveChanges`.
- Entités avec jeton de concurrence.

### Exemple sans l'amélioration

```csharp
// ❌ L'entité en mémoire est périmée après ExecuteUpdate
await Mesurer("AVANT : entité périmée", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var g = await db.Goods.FirstAsync(x => x.Id == 42);
        var avant = g.BasePrice;
        await db.Goods.Where(x => x.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(x => x.BasePrice, x => x.BasePrice + 1000));
        return $"en mémoire : {g.BasePrice} (inchangé : {g.BasePrice == avant}) ; en base : {await db.Goods.AsNoTracking().Where(x => x.Id == 42).Select(x => x.BasePrice).FirstAsync()}";
    }));

// ❌ Suppression bloquée par une contrainte (NO ACTION) : un bien ayant des échanges
await Mesurer("AVANT : suppression + FK", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        try { return $"{await db.Database.ExecuteSqlAsync($"DELETE FROM dbo.Goods WHERE Id = (SELECT TOP 1 GoodId FROM dbo.Trades)")}"; }
        catch (Exception e) { return e.GetType().Name + " : " + e.Message.Split((char)10)[0]; }     // conflit avec la contrainte FK
    }));
```

### Exemple avec l'amélioration

```csharp
// ✔ Resynchroniser explicitement l'entité (ou ne pas la charger du tout)
await Mesurer("APRÈS : Reload", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var g = await db.Goods.FirstAsync(x => x.Id == 42);
        await db.Goods.Where(x => x.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(x => x.BasePrice, x => x.BasePrice + 1000));
        await db.Entry(g).ReloadAsync();
        return $"après Reload : {g.BasePrice}";
    }));

// ✔ Audit explicite pour les mises à jour de masse (ce que SaveChangesInterceptor ne verra pas)
await Mesurer("APRÈS : audit manuel", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var n = await db.Goods.Where(g => g.IsContraband).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, g => g.BasePrice * 1.1m));
        await db.Database.ExecuteSqlAsync($"-- audit : {n} biens de contrebande réévalués");          // à remplacer par votre table d'audit
        return n;
    }));
```
**Concurrence** : écrire explicitement la condition :

```csharp
// jeton de concurrence « manuel » : on n'écrit que si la valeur lue est toujours celle de la base
await db.Goods.Where(g => g.Id == 42 && g.BasePrice == prixLu).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, prixNeuf));   // 0 ligne = conflit
```
**À observer** : valeurs en mémoire vs en base ; le message d'erreur de contrainte ; `0` lignes affectées en cas de conflit.

### Sans index / avec index et procédure stockée

Sans objet : ce sont des limites de **sémantique**.

### 🧪 TP 5.3 — « Qui voit mes modifications ? »

*Énoncé.* Dans un même contexte, chargez le bien n° 42, faites un `ExecuteUpdate` sur lui, puis (a) lisez la valeur de l'entité, (b) relisez avec `AsNoTracking`, (c) relisez avec `AsTracking`. Expliquez ce que renvoie chaque lecture et pourquoi (c) ne résout pas le problème.

<details><summary>Correction</summary>

(a) valeur **ancienne** (entité suivie non synchronisée) ; (b) valeur **nouvelle** (lecture directe de la base) ; (c) valeur **ancienne** : la requête ramène la ligne mais, l'entité étant **déjà suivie**, EF **conserve l'instance existante** et ses valeurs (la résolution d'identité prime sur les nouvelles valeurs, sauf `Reload()` ou `Entry(...).GetDatabaseValues()`). C'est pourquoi on évite de mélanger les deux approches dans un même contexte.
</details>

---

## 5.4 Le *batching* de `SaveChanges`

**Axe concerné : 🗄️ SQL (allers-retours) + ⚙️ .NET.**

### La notion

`SaveChanges` **regroupe** les commandes `INSERT`/`UPDATE`/`DELETE` d'un même appel en **lots** envoyés ensemble (un seul aller-retour par lot). Réglages (`UseSqlServer(Cs, o => o.MaxBatchSize(n).MinBatchSize(m))`) : `MaxBatchSize` (par défaut **42**) fixe le nombre maximal de commandes par lot ; `MinBatchSize` (par défaut 2 pour SQL Server) évite de batcher des petits nombres. SQL Server limite à **2 100 paramètres** par commande : cela borne la taille effective des lots (nombre de colonnes × lignes).

### Dans quels cas on est concerné

- Insertions/mises à jour de **milliers** d'entités par `SaveChanges`.
- Réseau avec latence (chaque aller-retour coûte) : 20 000 insertions à 42 par lot = ~480 allers-retours ; à 1 par lot = 20 000.

### Exemple sans l'amélioration

```csharp
// Fabrique de contexte avec taille de lot réglable
StarWarsContext NewDbBatch(int max) =>
    new(new DbContextOptionsBuilder<StarWarsContext>().UseSqlServer(Cs, o => o.MaxBatchSize(max)).AddInterceptors(compteur).Options);

async Task<string> Inserer(StarWarsContext db, int n)
{
    await using var tx = await db.Database.BeginTransactionAsync();
    for (var i = 0; i < n; i++) db.LabItems.Add(new LabItem { Nom = $"x{i}", Valeur = i });
    var r = await db.SaveChangesAsync();
    await tx.RollbackAsync();
    return $"{r:N0} lignes";
}

// ❌ MaxBatchSize = 1 : un aller-retour par ligne
await using (var d1 = NewDbBatch(1))   await Mesurer("AVANT : MaxBatchSize 1", _ => Inserer(d1, 20_000));
```

### Exemple avec l'amélioration

```csharp
await using (var d42 = NewDbBatch(42))     await Mesurer("Défaut (42)", _ => Inserer(d42, 20_000));
await using (var d1000 = NewDbBatch(1000)) await Mesurer("MaxBatchSize 1000", _ => Inserer(d1000, 20_000), afficherSql: false);
```
**À observer** : « SQL: N req » (20 000 / ≈ 476 / ≈ 40 ou plus, borné par la limite de 2 100 paramètres : avec 2 colonnes insérées → ~1 000 lignes par commande) ; le temps ; la forme du SQL (`INSERT … VALUES (@p0, @p1), (@p2, @p3)…` ou un `MERGE`) dans le journal (`afficherSql: true` sur un petit `n`).

### Sans index / avec index

Plus il y a d'index sur `Lab_Item`, plus **chaque ligne** coûte à l'insertion, indépendamment du batching :

```sql
CREATE INDEX IX_Lab_Item_Nom ON dbo.Lab_Item (Nom);
CREATE INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item (Valeur) INCLUDE (Nom);
-- relancer « Défaut (42) » : comparez les écritures logiques et le temps ; puis DROP INDEX
DROP INDEX IX_Lab_Item_Nom ON dbo.Lab_Item; DROP INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item;
```

### Comparaison avec une procédure stockée

Une procédure ensembliste (`INSERT … SELECT … FROM GENERATE_SERIES`) évite **tout** aller-retour par ligne : voir 5.5.

### 🧪 TP 5.4 — « Trouver la bonne taille de lot »

*Énoncé.* Faites varier `MaxBatchSize` (1, 10, 42, 100, 500, 1000) pour 20 000 insertions. Tracez : nombre de requêtes, temps. À partir de quelle taille le gain devient-il négligeable ? Pourquoi une valeur très grande peut-elle être contre-productive ?

<details><summary>Correction</summary>

Le gain est fort jusqu'à quelques dizaines de lignes par lot (les allers-retours dominent), puis plafonne : l'insertion elle-même (journal, index) devient le facteur limitant. Une valeur très grande produit de **grosses commandes** (plan unique par taille de lot différente → cache de plans plus peuplé, limite de 2 100 paramètres, verrous gardés plus longtemps). Une valeur de l'ordre de la centaine est un bon compromis — à valider **sur votre réseau** (la latence modifie l'optimum).
</details>

---

## 5.5 Insertions massives

**Axe concerné : 🗄️ SQL (journalisation, allers-retours) + ⚙️ .NET (suivi).**

### La notion

| Technique | Principe | Coût .NET | Coût SQL |
|---|---|---|---|
| `AddRange` + `SaveChanges` | entités suivies, `INSERT` batchés | suivi de N entités, `DetectChanges` | N insertions journalisées |
| Lots + `Clear()` (04.5) | idem par paquets | mémoire bornée | idem |
| **`SqlBulkCopy`** | flux binaire direct (protocole TDS *bulk insert*) | pas d'entité, pas de suivi | **journalisation minimale** possible, très rapide |
| `INSERT … SELECT` en SQL | tout dans la base | nul | le plus rapide pour données déjà en base |

`SqlBulkCopy` est la voie des **très gros volumes** ; il sort du cadre du suivi d'EF Core (valeurs générées non rapatriées, pas de validation, pas d'événements).

### Dans quels cas on est concerné

- Imports de 100 000 lignes et plus.
- Chargement initial, ETL.

### Exemple sans l'amélioration

```csharp
// ❌ 100 000 entités suivies
await Mesurer("AVANT : AddRange + SaveChanges", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        db.LabItems.AddRange(Enumerable.Range(0, 100_000).Select(i => new LabItem { Nom = $"x{i}", Valeur = i }));
        return $"{await db.SaveChangesAsync():N0} lignes";
    }));
```

### Exemple avec l'amélioration

```csharp
// ✔ SqlBulkCopy (nécessite : using Microsoft.Data.SqlClient; using System.Data;)
await Mesurer("APRÈS : SqlBulkCopy", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var table = new DataTable();
        table.Columns.Add("Nom", typeof(string)); table.Columns.Add("Valeur", typeof(int));
        for (var i = 0; i < 100_000; i++) table.Rows.Add($"x{i}", i);

        var cn = (SqlConnection)db.Database.GetDbConnection();
        var tx = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "dbo.Lab_Item", BatchSize = 10_000 };
        bulk.ColumnMappings.Add("Nom", "Nom"); bulk.ColumnMappings.Add("Valeur", "Valeur");
        await bulk.WriteToServerAsync(table);
        return "100 000 lignes";
    }));

// ✔ Tout dans la base (si les données y sont déjà ou sont générables)
await Mesurer("APRÈS : INSERT … SELECT", async db =>
    await DansTransactionAnnulee(db, async () =>
        await db.Database.ExecuteSqlAsync($"INSERT INTO dbo.Lab_Item (Nom, Valeur) SELECT CONCAT(N'x', value), value FROM GENERATE_SERIES(1, 100000)")));
```
**À observer** : le temps ; les **allocations** (le `DataTable` en alloue aussi : un `IDataReader` personnalisé les évite) ; le nombre de requêtes (`SqlBulkCopy` n'apparaît pas dans le compteur EF) ; l'écart de plusieurs ordres de grandeur sur l'objet `DbContext` (aucune entité suivie).

### Sans index / avec index

```sql
-- relancer chaque variante avec des index sur Lab_Item
CREATE INDEX IX_Lab_Item_Nom ON dbo.Lab_Item (Nom);
CREATE INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item (Valeur) INCLUDE (Nom);
-- l'écart entre variantes se resserre : chaque ligne doit être insérée dans 3 structures
DROP INDEX IX_Lab_Item_Nom ON dbo.Lab_Item; DROP INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item;
```
Pour les très gros chargements, une pratique courante est de **désactiver les index non clusterisés**, charger, puis les **reconstruire** (`ALTER INDEX … DISABLE` / `REBUILD`). `SqlBulkCopyOptions.TableLock` permet la **journalisation minimale** dans certaines conditions (modèle de récupération simple/bulk-logged, table vide ou tas) *(à vérifier selon votre configuration)*.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_InsererItems @N int
AS BEGIN SET NOCOUNT ON; INSERT INTO dbo.Lab_Item (Nom, Valeur) SELECT CONCAT(N'x', value), value FROM GENERATE_SERIES(1, @N); END
```
```csharp
await Mesurer("PROC : Lab_InsererItems", async db =>
    await DansTransactionAnnulee(db, async () => await db.Database.ExecuteSqlAsync($"EXEC dbo.Lab_InsererItems {100_000}")));
```
**À observer** : la procédure ensembliste est la **borne basse** du temps d'insertion de 100 000 lignes dans la base ; l'écart avec `SqlBulkCopy` mesure le coût du **transport** des données depuis .NET ; l'écart avec `AddRange` mesure le coût d'**EF** (suivi + lots). La procédure n'est comparable que si les données **n'ont pas besoin de venir du client**.

### 🧪 TP 5.5 — « Quatre façons d'insérer 100 000 lignes »

*Énoncé.* Remplissez le tableau pour les quatre méthodes (AddRange, lots de 10 000 + `Clear()`, `SqlBulkCopy`, procédure ensembliste), **sans** puis **avec** deux index supplémentaires : temps, mémoire (pic), requêtes, écritures logiques (`SET STATISTICS IO`).

| Méthode | Index | Temps | Pic mémoire | Requêtes | Logical reads |
|---|---|---|---|---|---|

<details><summary>Correction</summary>

Ordre attendu (du plus lent au plus rapide) : `AddRange` ≳ lots+`Clear()` > `SqlBulkCopy` > procédure ensembliste. Les index supplémentaires augmentent le temps de **toutes** les méthodes, et réduisent l'écart relatif car le coût d'écriture dans les index domine. Les lots + `Clear()` n'accélèrent pas forcément ; ils **bornent la mémoire**.
</details>

---

## 5.6 `SqlQuery<T>` et `ExecuteSql`

**Axe concerné : 🗄️ SQL (contrôle total) + ⚙️ .NET (matérialisation simple).**

### La notion

- **`db.Database.SqlQuery<T>($"…")`** : exécute du SQL et matérialise **n'importe quel type** (scalaires, DTO, `record`) — sans qu'il soit déclaré dans le modèle, sans suivi. Pour un scalaire, la colonne doit s'appeler **`Value`**. Le résultat peut ensuite être **composé** avec LINQ (le SQL devient une sous-requête).
- **`db.Database.ExecuteSql($"…")`** : exécute une commande (écriture, DDL) et retourne le nombre de lignes affectées.
- Les **interpolations** `$"…{x}…"` sont transformées en **paramètres** (pas de concaténation).

### Dans quels cas on est concerné

- Requête que LINQ ne sait pas exprimer (fenêtrage, `PIVOT`, CTE récursive) ou qu'on veut figer.
- Lecture de résultats d'une procédure ou d'une vue sans entité.

### Exemple sans l'amélioration

```csharp
// ❌ Contourner l'API : ADO.NET manuel pour un simple compteur + mapping à la main
await Mesurer("AVANT : ADO.NET à la main", async db =>
{
    var cn = (SqlConnection)db.Database.GetDbConnection(); await cn.OpenAsync();
    await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Goods WHERE IsContraband = 1", cn);
    return await cmd.ExecuteScalarAsync();
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Scalaire (colonne nommée Value)
await Mesurer("APRÈS : SqlQuery<int>", async db =>
    (await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Goods WHERE IsContraband = 1").ToListAsync()).Single(), afficherSql: true);

// ✔ DTO + composition LINQ au-dessus du SQL
public record BienPrix(int Id, string Sku, decimal BasePrice);
await Mesurer("APRÈS : DTO composable", async db =>
    (await db.Database.SqlQuery<BienPrix>($"SELECT Id, Sku, BasePrice FROM dbo.Goods WHERE IsContraband = 1")
        .Where(b => b.BasePrice > 90_000).OrderByDescending(b => b.BasePrice).Take(10).ToListAsync()).Count, afficherSql: true);

// ✔ Commande
await Mesurer("APRÈS : ExecuteSql", async db =>
    await DansTransactionAnnulee(db, async () => await db.Database.ExecuteSqlAsync($"UPDATE dbo.Goods SET BasePrice = BasePrice + {1m} WHERE Id = {42}")), afficherSql: true);
```
**À observer** : `SELECT … FROM (SELECT Id, Sku, BasePrice FROM dbo.Goods WHERE …) AS [b] WHERE … ORDER BY … OFFSET/TOP` : la composition **enveloppe** votre SQL ; `@p0` pour les valeurs interpolées.

### Sans index / avec index

Le SQL brut est optimisé comme un autre : les index sont utilisés selon les mêmes règles (sargabilité, voir 2.9). Le SQL brut **ne corrige pas** un prédicat non sargable.

### Comparaison avec une procédure stockée

`SqlQuery<T>` avec `EXEC proc` lit le résultat de la procédure ; la différence tient à l'endroit où le SQL est **stocké** (code vs base), pas à l'exécution. **Non composable** quand on appelle une procédure (`EXEC` ne s'enveloppe pas) ; **composable** quand on écrit un `SELECT`.

### 🧪 TP 5.6 — « Du SQL, mais pas n'importe comment »

*Énoncé.* Écrivez en `SqlQuery<T>` la requête « nombre d'échanges par type et par année » avec une fenêtre `COUNT(*) OVER (…)` si utile, puis filtrez/triez en LINQ. Vérifiez : paramétrage des valeurs, forme de la sous-requête. Montrez ce qui se passe si vous **concaténez** une valeur (`"... WHERE x = " + x`) au lieu de l'interpoler.

<details><summary>Correction</summary>

```csharp
public record EchangesParAn(int TradeType, int Annee, int N);
var l = await db.Database.SqlQuery<EchangesParAn>(
    $"SELECT TradeType, YEAR(TradedAt) AS Annee, COUNT(*) AS N FROM dbo.Trades GROUP BY TradeType, YEAR(TradedAt)")
    .Where(x => x.N > 1000).OrderBy(x => x.Annee).ToListAsync();
```
Avec une **concaténation** (`FromSqlRaw("… " + x)`), la valeur est écrite dans le texte : chaque valeur produit un SQL distinct (bloat du cache de plans, voir 2.5) et **ouvre l'injection SQL**. `FromSql` / `SqlQuery` avec interpolation protègent contre les deux.
</details>

---

## 5.7 `FromSql` composable

**Axe concerné : 🗄️ SQL.**

### La notion

`DbSet.FromSql($"…")` exécute du SQL qui retourne **les colonnes d'une entité du modèle** ; EF peut ensuite **composer** `Where`, `OrderBy`, `Skip/Take`, `Include`… en enveloppant votre SQL comme sous-requête. **Règles** : le SQL doit être un `SELECT` (pas de `EXEC` composable ; un `ORDER BY` terminal n'est pas autorisé sans `TOP`/`OFFSET` ; une CTE (`WITH`) ne peut pas être enveloppée), et retourner **toutes** les colonnes de l'entité.

### Dans quels cas on est concerné

- Améliorer **une partie** d'une requête (hint, fonction, jointure complexe) et laisser LINQ faire le reste.
- Éviter de reconstruire toute une requête en SQL brut.

### Exemple sans l'amélioration

```csharp
// ❌ Tout le SQL brut : on perd la composition et on duplique la logique de pagination
await Mesurer("AVANT : SQL complet", async db =>
    (await db.Goods.FromSql($"SELECT TOP (10) * FROM dbo.Goods WHERE IsContraband = 1 AND BasePrice > {90_000m} ORDER BY BasePrice DESC").ToListAsync()).Count, afficherSql: true);

// ❌ Concaténation de chaîne : injection et plan par valeur
var seuil = "90000";
await Mesurer("AVANT : concaténation", async db =>
    (await db.Goods.FromSqlRaw("SELECT * FROM dbo.Goods WHERE IsContraband = 1 AND BasePrice > " + seuil).Take(10).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```csharp
// ✔ Un SELECT simple + LINQ pour le reste
await Mesurer("APRÈS : FromSql + composition", async db =>
    (await db.Goods.FromSql($"SELECT * FROM dbo.Goods WHERE IsContraband = 1")
        .Where(g => g.BasePrice > 90_000m).OrderByDescending(g => g.BasePrice).Take(10).AsNoTracking().ToListAsync()).Count, afficherSql: true);

// ✔ Paramétrage par interpolation
decimal prix = 90_000m;
await Mesurer("APRÈS : interpolation", async db =>
    (await db.Goods.FromSql($"SELECT * FROM dbo.Goods WHERE IsContraband = 1 AND BasePrice > {prix}").Take(10).AsNoTracking().ToListAsync()).Count, afficherSql: true);
```
**À observer** : `SELECT TOP(@p) … FROM (SELECT * FROM dbo.Goods WHERE IsContraband = 1) AS [g] WHERE … ORDER BY …` ; **un seul plan** pour toutes les valeurs de `prix` (paramètre) ; le message d'erreur d'EF si vous tentez de composer un `ORDER BY` terminal ou une CTE.

### Sans index / avec index

Les index du `WHERE` de votre SQL brut et ceux des prédicats LINQ **ajoutés** sont utilisés ensemble (l'optimiseur aplatit la sous-requête). Mesurez avec `IX_Lab_Goods_Contraband` (voir 1.1).

### Comparaison avec une procédure stockée

`FromSql($"EXEC dbo.proc …")` fonctionne, mais **n'est pas composable** (EF ne peut pas envelopper un `EXEC`) ; les filtres/tris LINQ s'appliquent alors **en mémoire** sur le résultat de la procédure si vous ajoutez `AsEnumerable()`. Pour garder la composabilité, utilisez une **fonction table** (voir 2.4) ou une **vue**.

### 🧪 TP 5.7 — « Les limites de la composition »

*Énoncé.* Essayez de composer un `.Where(...)` sur : (a) un `SELECT` simple, (b) un `SELECT … ORDER BY Id` sans `TOP`, (c) une requête commençant par `WITH cte AS (…)`, (d) `EXEC dbo.Lab_PageHabitants …`. Notez, pour chacun, si cela fonctionne et le SQL ou l'erreur obtenus. Proposez le contournement.

<details><summary>Correction</summary>

(a) fonctionne (sous-requête) ; (b) l'`ORDER BY` terminal est refusé par SQL Server dans une sous-requête (erreur) : le retirer ou ajouter `TOP`/`OFFSET` ; (c) la CTE ne peut pas être enveloppée dans une sous-requête : la transformer en sous-requête dérivée, en vue ou en fonction table ; (d) un `EXEC` ne se compose pas : filtrer côté SQL (paramètres de la procédure) ou passer à une fonction table.
</details>

---

## 5.8 Migrations *bundle*

**Axe concerné : déploiement (pas de requêtes) — et temps de migration sur de grosses tables.**

### La notion

`dotnet ef migrations bundle` génère un **exécutable autonome** (`efbundle`) qui applique les migrations sans le SDK .NET ni le code source du projet, utilisable en pipeline de déploiement. Alternatives : `dotnet ef database update` (nécessite SDK + projet), `dotnet ef migrations script --idempotent` (script SQL rejouable), `Database.Migrate()` au démarrage (déconseillé en production).

> **« Bundler de requêtes »** : le terme n'existe pas tel quel en EF Core. Ce que le sujet recouvre vraisemblablement : (1) le **bundle de migrations** (cette section), (2) le **batching** des commandes d'écriture (5.4), (3) l'envoi de **plusieurs instructions en un aller-retour** (`ExecuteUpdate`, 5.1, ou plusieurs jeux de résultats via ADO.NET, chapitre 10). À confirmer avec vos organisateurs.

### Dans quels cas on est concerné

- Pipelines CI/CD sans SDK sur le serveur de déploiement.
- Migrations sur de **grosses tables** : le coût d'un `ALTER TABLE` ou d'un `CREATE INDEX` dépend du volume.

### Exemple sans l'amélioration

> **Ne faites jamais cela sur `StarWarsTrade`.** Créez une base à part (`LabMigr`) avec un petit projet de test.

```bash
# ❌ Déploiement par « dotnet ef database update » depuis la machine de build : suppose le SDK, le code et la chaîne de connexion partout
dotnet ef database update --connection "Server=localhost,1433;Database=LabMigr;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False"
```

### Exemple avec l'amélioration

```bash
# ✔ Générer le bundle (une fois, en build) puis l'exécuter n'importe où
dotnet ef migrations bundle --self-contained -r win-x64 -o efbundle.exe
./efbundle.exe --connection "Server=localhost,1433;Database=LabMigr;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False"

# ✔ Ou un script idempotent à relire/valider par un DBA
dotnet ef migrations script --idempotent -o migrations.sql
```
**Mesurer le coût d'une migration sur une grosse table** (dans la base `LabMigr`, avec une table de 1 M de lignes générée par `GENERATE_SERIES`) :

```sql
SET STATISTICS TIME ON;
ALTER TABLE dbo.Gros ADD Note nvarchar(20) NULL;                          -- colonne nullable : opération de métadonnées, quasi instantanée
ALTER TABLE dbo.Gros ADD Statut int NOT NULL DEFAULT 0;                   -- valeur par défaut constante : métadonnées (SQL Server 2012+)
CREATE INDEX IX_Gros_Valeur ON dbo.Gros (Valeur);                         -- construction d'index : lecture + tri de toute la table
CREATE INDEX IX_Gros_Valeur2 ON dbo.Gros (Valeur) WITH (ONLINE = ON);     -- en ligne : disponible selon l'édition
```
**À observer** : le temps de chaque instruction ; le verrou posé pendant la migration (`sys.dm_tran_locks`) ; la différence entre une opération de métadonnées et une opération qui **réécrit** la table (changement de type, `NOT NULL` avec défaut non constant).

### Sans index / avec index

Les migrations qui **ajoutent un index** sur une grosse table sont la principale cause d'indisponibilité : prévoir `ONLINE = ON` (si l'édition le permet), des fenêtres de maintenance, ou la création en deux temps.

### Comparaison avec une procédure stockée

Sans objet : c'est du déploiement de schéma.

### 🧪 TP 5.8 — « Déployer sans SDK »

*Énoncé.* Dans une base `LabMigr`, créez un petit projet avec une entité et deux migrations (ajout d'une table, ajout d'un index). Produisez le bundle, appliquez-le sur une base vide, puis sur une base déjà migrée. Mesurez le temps de chaque étape et listez ce que le bundle **ne** fait **pas** (par exemple : pas de rollback automatique en cas d'échec partiel d'une migration non transactionnelle).

<details><summary>Correction</summary>

Séquence : `dotnet ef migrations add Init`, `…add AjoutIndex`, `dotnet ef migrations bundle --force`, puis `efbundle.exe --connection "…"` deux fois (la deuxième exécution ne fait rien : l'historique `__EFMigrationsHistory` est à jour). Le bundle applique les migrations **non appliquées** ; il ne gère pas le retour arrière (pour cela : `efbundle.exe <MigrationCible>` pour revenir en arrière, ou une migration inverse) ni la planification des gros `ALTER`.
</details>

---

## 5.9 Concurrence optimiste

**Axe concerné : 🗄️ SQL (verrous évités) + justesse sous charge.**

### La notion

Plutôt que de **verrouiller** une ligne le temps d'une modification (pessimiste), l'**optimiste** lit sans verrou et, à l'écriture, vérifie que la ligne **n'a pas changé** (`UPDATE … WHERE Id = @id AND RowVersion = @original`). Si 0 ligne est affectée : `DbUpdateConcurrencyException`. Jeton : colonne `rowversion` (`[Timestamp]` / `IsRowVersion()`) ou `IsConcurrencyToken()`. Avantage : pas de verrou long, bonne montée en charge quand les conflits sont **rares**. Coût : retries quand ils sont fréquents.

### Dans quels cas on est concerné

- Écrans d'édition où l'utilisateur lit, réfléchit, puis enregistre.
- Compteurs et soldes modifiés par plusieurs processus.

### Mise en place

```sql
CREATE TABLE dbo.Lab_Version (Id int PRIMARY KEY, Valeur int NOT NULL, RowVersion rowversion NOT NULL);
INSERT INTO dbo.Lab_Version (Id, Valeur) VALUES (1, 0);
```
```csharp
public class LabVersion { public int Id { get; set; } public int Valeur { get; set; } [Timestamp] public byte[] RowVersion { get; set; } = null!; }
// Classe partielle : public DbSet<LabVersion> LabVersions => Set<LabVersion>();  et ToTable("Lab_Version") dans OnModelCreatingPartial
```

### Exemple sans l'amélioration

```csharp
// ❌ Pas de jeton : le dernier qui écrit gagne, l'autre modification est perdue en silence
// (simulez avec une entité sans [Timestamp] : deux contextes lisent Valeur = 0, l'un écrit 10, l'autre écrit 5 → 5)
```

### Exemple avec l'amélioration

```csharp
// ✔ Conflit détecté
await using var a = NewDb(); await using var b = NewDb();
var ea = await a.LabVersions.FirstAsync(x => x.Id == 1);
var eb = await b.LabVersions.FirstAsync(x => x.Id == 1);
ea.Valeur = 10; await a.SaveChangesAsync();                       // OK, rowversion change
eb.Valeur = 5;
try { await b.SaveChangesAsync(); }
catch (DbUpdateConcurrencyException e) { Console.WriteLine($"Conflit : {e.Entries.Count} entrée(s) en conflit"); }

// ✔ Résolution « base gagne » ou « client gagne » avec relance
for (var essai = 0; essai < 3; essai++)
{
    try { await b.SaveChangesAsync(); break; }
    catch (DbUpdateConcurrencyException e)
    {
        var entry = e.Entries.Single();
        var valeursBase = await entry.GetDatabaseValuesAsync();
        entry.OriginalValues.SetValues(valeursBase!);              // « client gagne » : on garde nos valeurs sur la nouvelle version
    }
}
```
**À observer** : le SQL de l'`UPDATE` : `UPDATE … SET Valeur = @p0 WHERE Id = @p1 AND RowVersion = @p2` ; l'exception ; après la résolution, le `RowVersion` de la ligne a changé.

**Optimiste contre pessimiste sous charge** (8 écrivains concurrents sur la même ligne) :

```csharp
await Mesurer("Optimiste : 8 tâches × 50 incréments", async db =>
{
    var conflits = 0;
    await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
    {
        for (var i = 0; i < 50; i++)
            while (true)
            {
                await using var c = NewDb();
                var e = await c.LabVersions.FirstAsync(x => x.Id == 1); e.Valeur++;
                try { await c.SaveChangesAsync(); break; } catch (DbUpdateConcurrencyException) { Interlocked.Increment(ref conflits); }
            }
    }));
    return $"conflits : {conflits}, valeur finale : {(await db.LabVersions.AsNoTracking().FirstAsync(x => x.Id == 1)).Valeur}";
});

await Mesurer("Pessimiste : UPDLOCK, 8 tâches × 50 incréments", async db =>
{
    await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
    {
        for (var i = 0; i < 50; i++)
        {
            await using var c = NewDb(); await using var tx = await c.Database.BeginTransactionAsync();
            await c.Database.ExecuteSqlAsync($"UPDATE dbo.Lab_Version SET Valeur = Valeur + 1 WHERE Id = 1");   // verrou d'écriture immédiat
            await tx.CommitAsync();
        }
    }));
    return $"valeur finale : {(await db.LabVersions.AsNoTracking().FirstAsync(x => x.Id == 1)).Valeur}";
});
```
(La valeur finale doit être 400 plus la valeur initiale dans les deux cas : aucune mise à jour perdue.)

### Sans index / avec index

Le jeton `rowversion` est lu avec la ligne : pas d'index supplémentaire. Le coût d'un conflit est celui d'une **relecture + nouvelle écriture** ; un `UPDATE` ensembliste (`valeur = valeur + 1`) évite complètement le conflit.

### Comparaison avec une procédure stockée

Une procédure qui fait `UPDATE … SET Valeur = Valeur + @delta` est **atomique** sans jeton : c'est la meilleure solution pour un compteur. L'optimiste est destiné aux cas où l'on **lit, modifie en logique applicative, puis écrit**.

### 🧪 TP 5.9 — « Le compteur sans conflit »

*Énoncé.* Reprenez le test à 8 tâches. Version A : optimiste avec rowversion ; version B : `ExecuteUpdate(s => s.SetProperty(x => x.Valeur, x => x.Valeur + 1))` ; version C : `UPDLOCK`. Comparez temps, nombre de conflits, valeur finale. Quelle est la meilleure pour un compteur ?

<details><summary>Correction</summary>

B est la meilleure pour un compteur : une seule instruction atomique, sans lecture préalable, sans conflit, sans transaction explicite. A est justifiée quand la décision dépend de ce qui a été lu (« mettre à jour **si** la valeur est inchangée ») ; C est sûre mais sérialise les écrivains.
</details>

---

## 5.10 `ExecutionStrategy` et transactions

**Axe concerné : 🗄️ SQL (coût du `COMMIT`, durée des verrous) + robustesse.**

### La notion

- Chaque `SaveChanges` s'exécute dans **sa propre transaction** (un `COMMIT` = une écriture synchrone du journal). 5 000 `SaveChanges` = 5 000 commits.
- Une **transaction explicite** regroupe plusieurs `SaveChanges` en un commit : moins de vidages du journal, mais verrous gardés plus longtemps.
- `EnableRetryOnFailure()` active une **stratégie d'exécution** qui relance automatiquement après une erreur **transitoire** (réseau, base cloud). Avec elle, une transaction **initiée par vous** doit être enveloppée dans `strategy.ExecuteAsync(...)` : EF doit pouvoir **rejouer toute l'unité**.

### Dans quels cas on est concerné

- Boucles qui appellent `SaveChanges` à chaque élément.
- Applications cloud (Azure SQL) avec reprise sur erreur transitoire.

### Exemple sans l'amélioration

```csharp
// ❌ Un SaveChanges (donc un COMMIT) par ligne
await Mesurer("AVANT : SaveChanges par ligne (2 000)", async db =>
{
    for (var i = 0; i < 2_000; i++) { db.LabItems.Add(new LabItem { Nom = $"x{i}", Valeur = i }); await db.SaveChangesAsync(); }
    return "2 000 lignes commitées";
});
// → puis nettoyer : TRUNCATE TABLE dbo.Lab_Item;

// ❌ Transaction manuelle avec EnableRetryOnFailure : exception d'exécution
//    (dans NewDb : .UseSqlServer(Cs, o => o.EnableRetryOnFailure()))
await using var db2 = NewDbAvecRetry();
try { await using var tx = await db2.Database.BeginTransactionAsync(); }
catch (InvalidOperationException e) { Console.WriteLine(e.Message.Split('.')[0]); }   // « The configured execution strategy … does not support user-initiated transactions »
```

### Exemple avec l'amélioration

```csharp
// ✔ Une transaction, un seul COMMIT
await Mesurer("APRÈS : une transaction", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    for (var i = 0; i < 2_000; i++) { db.LabItems.Add(new LabItem { Nom = $"x{i}", Valeur = i }); await db.SaveChangesAsync(); }
    await tx.RollbackAsync();
    return "2 000 lignes (annulées)";
});

// ✔ Mieux : un seul SaveChanges (batching)
await Mesurer("APRÈS : un SaveChanges", async db =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    db.LabItems.AddRange(Enumerable.Range(0, 2_000).Select(i => new LabItem { Nom = $"x{i}", Valeur = i }));
    await db.SaveChangesAsync(); await tx.RollbackAsync();
    return "2 000 lignes (annulées)";
});

// ✔ Transaction explicite compatible avec la stratégie de reprise
await using var db3 = NewDbAvecRetry();
var strategy = db3.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var tx = await db3.Database.BeginTransactionAsync();
    db3.LabItems.Add(new LabItem { Nom = "retry", Valeur = 1 });
    await db3.SaveChangesAsync();
    await tx.RollbackAsync();
});
```
**À observer** : le nombre de commandes ; **le temps de la version « un commit par ligne »**, dominé par l'attente d'écriture du journal à chaque `COMMIT` ; dans SQL Server, `sys.dm_io_virtual_file_stats` (écritures du journal) ou les attentes `WRITELOG` (`sys.dm_os_wait_stats`) augmentent nettement.

### Sans index / avec index

Les insertions dans `Lab_Item` sont identiques dans les trois cas ; c'est la **fréquence des commits** qui varie.

### Comparaison avec une procédure stockée

Une procédure qui fait `BEGIN TRAN … COMMIT` en une seule exécution a **un** `COMMIT` pour tout le traitement ; c'est l'équivalent SQL de « une transaction ». Elle n'élimine pas le besoin de gérer les erreurs transitoires côté client.

### 🧪 TP 5.10 — « Combien coûte un commit ? »

*Énoncé.* Insérez 2 000 lignes de trois façons (un `SaveChanges` par ligne, une transaction autour de 2 000 `SaveChanges`, un seul `SaveChanges`). Mesurez temps et attentes `WRITELOG` (avant/après via `sys.dm_os_wait_stats`). Estimez le coût d'un commit.

<details><summary>Correction</summary>

Relever avant et après chaque variante : `SELECT wait_time_ms, waiting_tasks_count FROM sys.dm_os_wait_stats WHERE wait_type = 'WRITELOG';`. Le coût d'un commit ≈ (temps variante 1 − temps variante 3) / 2 000, dominé par la latence d'écriture du journal sur le disque. Ne **pas** oublier de vider `Lab_Item` après la variante qui valide réellement.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Goods_Contra     ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Goods_BasePrice  ON dbo.Goods;
DROP INDEX IF EXISTS IX_Lab_Item_Nom         ON dbo.Lab_Item;
DROP INDEX IF EXISTS IX_Lab_Item_Valeur      ON dbo.Lab_Item;

DROP PROCEDURE IF EXISTS dbo.Lab_MajPrixContrebande;
DROP PROCEDURE IF EXISTS dbo.Lab_InsererItems;

DROP TABLE IF EXISTS dbo.Lab_Item;
DROP TABLE IF EXISTS dbo.Lab_Version;

-- Si vous avez créé la base de migrations :
-- DROP DATABASE IF EXISTS LabMigr;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
-- Données d'origine : à comparer avec les valeurs de départ
SELECT COUNT(*) FROM dbo.Goods WHERE IsContraband = 1;  SELECT BasePrice FROM dbo.Goods WHERE Id = 42;
```
