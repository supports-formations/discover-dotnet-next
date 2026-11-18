# 08 — Volumétrie : travailler sur des millions de lignes

> Suite de [`07-techniques-avancees.md`](07-techniques-avancees.md). Boîte à outils : celle du [00](00-prerequis.md) (`NewDb`, `Mesurer`, `CompteurSql`, `SET STATISTICS IO, TIME ON`, plans, sondes mémoire), `MesurerReseau` du [03](03-donnees-liees.md), `DansTransactionAnnulee` du [05](05-ecritures.md).
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé : les effets décrits sont **attendus et à vérifier**.
> **Prudence ressources** : plusieurs démonstrations manipulent des millions de lignes. Surveillez la mémoire du conteneur SQL Server (`docker stats masteringlinq-sql`), celle de votre processus .NET (`dotnet-counters`) et l'espace disque. En cas de doute, **réduisez les plages** (`Id <= 1_000_000`).
> Objets de démonstration : `IX_Lab_`, `Lab_`. **Script de nettoyage** en fin de fichier — **ne le sautez pas** : ce chapitre crée des index volumineux (dont un *columnstore*) et modifie temporairement une option de la base (8.14).
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

| # | Notion | Lien |
|---|---|---|
| [8.1](#81-ne-jamais-matérialiser-lensemble) | Ne jamais matérialiser l'ensemble | 0.3, 7.7.1 |
| [8.2](#82-lots-par-curseur-de-clé) | Traitement par lots à curseur de clé | 1.2 |
| [8.3](#83-keyset-sur-un-parcours-complet) | Keyset sur un parcours complet | 7.3.1 |
| [8.4](#84-opérations-ensemblistes-et-journal) | Opérations ensemblistes, journal et verrous | 5.1 |
| [8.5](#85-index-couvrants-filtrés-et-columnstore) | Index couvrants, filtrés, *columnstore* | 6.7 |
| [8.6](#86-lire-un-plan-dexécution) | Lire un plan d'exécution | 00 |
| [8.7](#87-tables-temporelles) | Tables temporelles | — |
| [8.8](#88-count-coûteux) | `COUNT(*)` coûteux | 7.3.3 |
| [8.9](#89-agrégats-lourds) | Agrégats lourds : pré-agrégation, vues indexées | 4.7 |
| [8.10](#810-tri-distinct-et-groupby-volumineux) | Tri, `Distinct`, `GroupBy` volumineux (*spills*) | — |
| [8.11](#811-mémoire-du-client) | Mémoire du client | 4.3 |
| [8.12](#812-lecture-parallèle) | Lecture parallèle | — |
| [8.13](#813-timeouts-annulation-et-verrous) | Timeouts, annulation, verrous | 7.5.3 |
| [8.14](#814-niveaux-disolation) | Niveaux d'isolation | 3.9 |
| [8.15](#815-lecture-incrémentale) | Lecture incrémentale | — |

---

## 8.1 Ne jamais matérialiser l'ensemble

**Axe concerné : 🧠 mémoire + ⚙️ .NET + 🗄️ SQL (session ouverte).** *Base : 0.3, 7.7.1.*

### La notion

Exporter, copier, recalculer sur des **millions** de lignes ne doit pas passer par un `ToList()` : la mémoire croît avec le résultat. On **diffuse** (`AsAsyncEnumerable`, projection étroite, écriture au fil de l'eau) ou on **découpe** en lots (8.2). Côté SQL, la requête reste ouverte tant que le client lit : un client lent retient la session.

### Dans quels cas on est concerné

Exports CSV, synchronisation vers un autre système, calculs batch.

### Exemple sans l'amélioration

```csharp
// ❌ 2 M d'échanges en mémoire avant d'écrire la première ligne
await Mesurer("AVANT : ToList puis écriture", async db =>
{
    var l = await db.Trades.AsNoTracking().Where(t => t.Id <= 2_000_000).ToListAsync();
    await using var w = new StreamWriter(Path.GetTempFileName());
    foreach (var t in l) await w.WriteLineAsync($"{t.Id};{t.GoodId};{t.Quantity};{t.UnitPrice}");
    return $"{l.Count:N0} lignes";
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Flux : projection étroite, écriture au fil de l'eau
await Mesurer("APRÈS : flux", async db =>
{
    var n = 0; await using var w = new StreamWriter(Path.GetTempFileName());
    await foreach (var t in db.Trades.AsNoTracking().Where(t => t.Id <= 2_000_000).Select(t => new { t.Id, t.GoodId, t.Quantity, t.UnitPrice }).AsAsyncEnumerable())
    { await w.WriteLineAsync($"{t.Id};{t.GoodId};{t.Quantity};{t.UnitPrice}"); n++; }
    return $"{n:N0} lignes";
});
```
**À observer** : pic de tas (sonde du 00, section D) ; `GC2` ; Mo alloués ; le fichier produit.

### Sans index / avec index

Lecture séquentielle de l'index clustered (`Id <= …`) : aucun index à ajouter.

### Comparaison avec une procédure stockée / l'outil `bcp`

L'export massif **côté SQL** est encore plus direct :

```bash
docker exec masteringlinq-sql /opt/mssql-tools18/bin/bcp "SELECT Id, GoodId, Quantity, UnitPrice FROM StarWarsTrade.dbo.Trades WHERE Id <= 2000000" queryout /tmp/trades.csv -c -t ";" -S localhost -U sa -P 'StarWars_Pwd123!' -C
```
Comparez les temps : `bcp` (SQL → fichier, sans .NET) ≤ flux EF ≤ `ToList` + écriture. Si vous n'avez pas d'autre besoin que d'écrire un fichier, `bcp` ou `SqlDataReader` brut battent EF ; si vous devez **transformer** chaque ligne en .NET, le flux EF est le bon compromis.

### 🧪 TP 8.1 — « Exporter sans dépasser 100 Mo »

*Énoncé.* Exportez 2 M d'échanges en CSV en gardant le **pic du tas** sous 100 Mo. Donnez trois variantes : flux EF, `SqlDataReader` brut, `bcp`. Mesurez temps et pic.

<details><summary>Correction</summary>

Flux EF avec projection étroite : < 100 Mo en général (pas de liste, pas de suivi). `SqlDataReader` brut : encore moins d'allocations. `bcp` : aucun coût .NET. Le choix dépend de la présence d'une logique C# dans la boucle.
</details>

---

## 8.2 Lots par curseur de clé

**Axe concerné : 🗄️ SQL (journal, verrous) + 🧠 mémoire.**

### La notion

Traiter une grande table **par lots** délimités par la **clé** (`Id` entre `a` et `b`) : mémoire bornée, transactions courtes, reprise possible. Pour les **écritures**, c'est aussi ce qui évite un journal énorme et l'**escalade de verrous** (au-delà d'environ 5 000 verrous de ligne, SQL Server les remplace par un verrou de **table** : les autres sessions sont bloquées).

### Dans quels cas on est concerné

Mises à jour/suppressions massives en production ; recalculs sur une table active.

### Exemple sans l'amélioration

```csharp
// ❌ Une seule mise à jour de 1 M de lignes : un énorme verrou et une énorme transaction
await Mesurer("AVANT : UPDATE de 1 M de lignes", async db =>
    await DansTransactionAnnulee(db, async () =>
    {
        var n = await db.Goods.Where(g => g.Id <= 1_000_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));
        var verrous = (await db.Database.SqlQuery<string>($"SELECT CONCAT(resource_type, '/', request_mode) AS Value FROM sys.dm_tran_locks WHERE request_session_id = @@SPID AND resource_type IN ('OBJECT','PAGE','KEY','RID') GROUP BY resource_type, request_mode").ToListAsync());
        var journal = (await db.Database.SqlQuery<long>($"SELECT database_transaction_log_bytes_used AS Value FROM sys.dm_tran_database_transactions WHERE database_id = DB_ID() AND transaction_id = (SELECT transaction_id FROM sys.dm_tran_current_transaction)").ToListAsync()).FirstOrDefault();
        return $"{n:N0} lignes ; verrous : {string.Join(", ", verrous)} ; journal : {journal / 1_048_576.0:N0} Mo";
    }));
```

### Exemple avec l'amélioration

```csharp
// ✔ Lots de 20 000 lignes par plage de clé, une transaction par lot (ici annulés pour ne rien modifier)
await Mesurer("APRÈS : lots de 20 000", async db =>
{
    long total = 0; long journalMax = 0;
    for (var debut = 1; debut <= 1_000_000; debut += 20_000)
        total += await DansTransactionAnnulee(db, async () =>
        {
            var n = await db.Goods.Where(g => g.Id >= debut && g.Id < debut + 20_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));
            var journal = (await db.Database.SqlQuery<long>($"SELECT database_transaction_log_bytes_used AS Value FROM sys.dm_tran_database_transactions WHERE database_id = DB_ID() AND transaction_id = (SELECT transaction_id FROM sys.dm_tran_current_transaction)").ToListAsync()).FirstOrDefault();
            journalMax = Math.Max(journalMax, journal); return n;
        });
    return $"{total:N0} lignes ; journal max par lot : {journalMax / 1_048_576.0:N1} Mo";
});
```
**À observer** : le **type de verrou** (OBJECT exclusif contre PAGE/KEY) ; le journal par transaction ; le **temps total** (les lots coûtent un peu plus au total, mais rendent le système **utilisable** pendant le traitement).

### Sans index / avec index

La plage `Id >= a AND Id < b` s'appuie sur la clé clusterisée. Si le critère est sur une colonne non indexée, **chaque lot rescanne** la table : créez d'abord l'index ou utilisez une clé indexée comme curseur.

### Comparaison avec une procédure stockée

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_MajParLots @Debut int, @Fin int, @Taille int
AS BEGIN SET NOCOUNT ON; DECLARE @n int = 1;
  WHILE @Debut <= @Fin AND @n > 0
  BEGIN
    UPDATE dbo.Goods SET WeightKg = WeightKg + 1 WHERE Id >= @Debut AND Id < @Debut + @Taille;
    SET @n = @@ROWCOUNT; SET @Debut += @Taille;
  END
END
```
La boucle côté SQL économise les allers-retours entre lots ; la logique par lots reste la même.

### 🧪 TP 8.2 — « Choisir la taille du lot »

*Énoncé.* Pour 1 M de lignes, faites varier la taille du lot (1 000 ; 5 000 ; 20 000 ; 100 000). Relevez : type de verrou, journal max par transaction, temps total. Quelle taille évitez-vous et pourquoi ?

<details><summary>Correction</summary>

Autour de **5 000 lignes** SQL Server **escalade** les verrous de ligne en verrou de table : au-dessus de cette taille, chaque lot bloque la table entière pendant sa durée. On choisit donc des lots **inférieurs** (quelques milliers) si la table est active, plus gros sinon.
</details>

---

## 8.3 Keyset sur un parcours complet

**Axe concerné : 🗄️ SQL (coût constant) + reprise. ** *Base : 7.3.1.*

### La notion

Parcourir **toute** une table par lots avec `Where(clé > dernière)` : coût **constant** par lot. Avantage supplémentaire : le curseur (`dernière`) se **persiste** : après un incident, on **reprend** où l'on s'était arrêté.

### Dans quels cas on est concerné

Migrations, synchronisations, réindexations applicatives qui durent des heures.

### Exemple sans l'amélioration

```csharp
// ❌ Parcours par OFFSET : chaque lot relit tout ce qui précède
await Mesurer("AVANT : Skip/Take sur 2 M", async db =>
{
    long somme = 0;
    for (var page = 0; page < 40; page++)
        somme += (await db.Inhabitants.AsNoTracking().OrderBy(i => i.Id).Skip(page * 50_000).Take(50_000).Select(i => i.Id).ToListAsync()).Count;
    return somme;
});
```

### Exemple avec l'amélioration

```csharp
// ✔ Keyset + curseur persisté
await Mesurer("APRÈS : keyset avec reprise", async db =>
{
    var curseur = File.Exists("curseur.txt") ? int.Parse(File.ReadAllText("curseur.txt")) : 0;
    long somme = 0;
    for (var lot = 0; lot < 40; lot++)
    {
        var ids = await db.Inhabitants.AsNoTracking().Where(i => i.Id > curseur).OrderBy(i => i.Id).Take(50_000).Select(i => i.Id).ToListAsync();
        if (ids.Count == 0) break;
        somme += ids.Count; curseur = ids[^1];
        File.WriteAllText("curseur.txt", curseur.ToString());              // point de reprise
    }
    return somme;
});
```
**À observer** : temps des **derniers** lots dans les deux cas ; reprise : interrompez, relancez, constatez que le traitement repart du curseur.

### Sans index / avec index

La clé primaire clusterisée suffit. Pour un parcours dans un autre ordre (`BirthYear, Id`), l'index `(BirthYear, Id)` est indispensable.

### Comparaison avec une procédure stockée

Une procédure `Lab_LotSuivant @Apres, @Taille` retourne le lot suivant : même plan.

### 🧪 TP 8.3 — « Reprendre après panne »

*Énoncé.* Ajoutez au parcours un traitement qui échoue au lot 17. Montrez que la reprise ne retraite pas les lots 1 à 16. Qu'ajouteriez-vous pour la **sûreté** (écriture du curseur et traitement dans la même transaction) ?

<details><summary>Correction</summary>

Stocker le curseur dans une **table** et le mettre à jour **dans la même transaction** que le traitement du lot : lot et curseur sont validés ou annulés ensemble. Un fichier ne garantit pas l'atomicité avec la base.
</details>

---

## 8.4 Opérations ensemblistes et journal

**Axe concerné : 🗄️ SQL (journal, verrous). ** *Base : 5.1.*

### La notion

`ExecuteUpdate`/`ExecuteDelete` évitent l'objet .NET, mais pas le **coût SQL** : le journal enregistre chaque ligne modifiée, les index sont maintenus, les verrous s'accumulent. Une **suppression** massive est la plus coûteuse (journal + index). Pour les très gros volumes : **lots**, **partitionnement** (changement de partition), ou **`TRUNCATE`** quand on vide tout.

### Dans quels cas on est concerné

Purges d'historique, archivage.

### Mise en place

```sql
IF OBJECT_ID('dbo.Lab_Item') IS NULL CREATE TABLE dbo.Lab_Item (Id int IDENTITY(1,1) PRIMARY KEY, Nom nvarchar(30) NOT NULL, Valeur int NOT NULL);
TRUNCATE TABLE dbo.Lab_Item;
INSERT INTO dbo.Lab_Item (Nom, Valeur) SELECT CONCAT(N'x', value), value FROM GENERATE_SERIES(1, 1000000);
```
(Entité `LabItem` du chapitre 05.)

### Exemple sans l'amélioration

```csharp
// ❌ RemoveRange de 500 000 entités suivies
await Mesurer("AVANT : RemoveRange + SaveChanges", async db =>
{
    var l = await db.LabItems.Where(i => i.Valeur % 2 == 0).ToListAsync();
    db.LabItems.RemoveRange(l);
    return $"{await db.SaveChangesAsync():N0} lignes";
});
```
*(Refaites la mise en place avant chaque variante : la suppression est validée.)*

### Exemple avec l'amélioration

```csharp
// ✔ Une instruction
await Mesurer("APRÈS (1) : ExecuteDelete", async db => $"{await db.LabItems.Where(i => i.Valeur % 2 == 0).ExecuteDeleteAsync():N0} lignes");

// ✔ Par lots de 50 000 (plage de clé)
await Mesurer("APRÈS (2) : lots", async db =>
{
    var total = 0;
    for (var debut = 1; debut <= 1_000_000; debut += 50_000)
        total += await db.LabItems.Where(i => i.Id >= debut && i.Id < debut + 50_000 && i.Valeur % 2 == 0).ExecuteDeleteAsync();
    return $"{total:N0} lignes";
});
```
**À observer** : `DBCC SQLPERF(LOGSPACE)` (utilisation du journal) avant/après ; temps ; `TRUNCATE TABLE` pour vider toute la table (quasi instantané, journal minimal).

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item (Valeur) INCLUDE (Nom);
-- relancer la suppression : plus lente (chaque ligne supprimée doit aussi l'être dans l'index)
DROP INDEX IX_Lab_Item_Valeur ON dbo.Lab_Item;
```

### Comparaison avec une procédure stockée

`DELETE TOP (50000) FROM dbo.Lab_Item WHERE Valeur % 2 = 0` dans une boucle `WHILE @@ROWCOUNT > 0` : même technique, sans allers-retours.

### 🧪 TP 8.4 — « Purger sans bloquer »

*Énoncé.* Supprimez 500 000 lignes pendant qu'une autre session lit la table. Comparez la suppression en une fois et par lots : durée du blocage de la lecture (`sys.dm_os_waiting_tasks`).

<details><summary>Correction</summary>

En une fois : l'escalade de verrous pose un verrou de table qui bloque la lecture jusqu'à la fin. Par lots : chaque lot bloque brièvement ; la lecture progresse entre les lots (voir 8.13 et 8.14).
</details>

---

## 8.5 Index couvrants, filtrés et *columnstore*

**Axe concerné : 🗄️ SQL (lectures logiques, mode batch) — avec le coût d'écriture.** *Base : 6.7.*

### La notion

| Type d'index | Idéal pour | Coût |
|---|---|---|
| **Couvrant** (`INCLUDE`) | requêtes ponctuelles sur peu de colonnes | taille + écriture |
| **Filtré** (`WHERE …`) | sous-ensemble sélectif (8 % de contrebande) | petit, mais prédicat connu à la compilation |
| **Columnstore** | **agrégats analytiques** sur des millions de lignes | taille compressée ; écriture via *delta store* |

### Dans quels cas on est concerné

Requêtes d'analyse (somme/moyenne/regroupement) sur de grandes tables ; sous-ensembles récurrents.

### Exemple sans l'amélioration

```csharp
// ❌ Agrégat analytique : 6 M de lignes lues en mode ligne
await Mesurer("AVANT : agrégat sans index", async db =>
    (await db.Trades.GroupBy(t => t.GoodId % 100).Select(g => new { g.Key, V = g.Sum(t => t.Quantity * t.UnitPrice), N = g.Count() }).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```sql
-- ✔ A. Index couvrant pour ce que cette requête lit
CREATE INDEX IX_Lab_Trades_Cover ON dbo.Trades (GoodId) INCLUDE (Quantity, UnitPrice);
-- ✔ B. Index filtré : seulement les échanges de vente récents
CREATE INDEX IX_Lab_Trades_Ventes2025 ON dbo.Trades (TradedAt) INCLUDE (CompanyId, Quantity, UnitPrice) WHERE TradeType = 1 AND TradedAt >= '2025-01-01';
-- ✔ C. Columnstore non clusterisé (construction : plusieurs secondes à dizaines de secondes)
CREATE NONCLUSTERED COLUMNSTORE INDEX IX_Lab_Trades_CS ON dbo.Trades (GoodId, CompanyId, Quantity, UnitPrice, TradedAt, TradeType);
```
Relancez le même C# après chaque index (créez-les **un à la fois**, supprimez avant le suivant).

**À observer** :
- A : le plan lit un index étroit (*Index Scan* sur `IX_Lab_Trades_Cover`) au lieu de la table : moins de pages.
- B : n'est utilisé que si la requête a **le même prédicat** (`TradeType = 1 AND TradedAt >= …`).
- C : le plan montre un opérateur **Columnstore Index Scan** et un mode d'exécution **Batch** ; temps et lectures **très** inférieurs pour les agrégats ; *taille* de l'index (`sp_spaceused`, requête ci-dessous) ; **coût d'écriture** : insérez des lignes (transaction annulée) avec et sans l'index.

```sql
SELECT i.name, CAST(SUM(ps.used_page_count) * 8 / 1024.0 AS decimal(10,1)) AS taille_mo
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.Trades') GROUP BY i.name ORDER BY taille_mo DESC;
```

### Sans index / avec index

C'est la notion : voir ci-dessus. **Coût caché** : chaque index alourdit les écritures (5.1, 5.5).

### Comparaison avec une procédure stockée

Aucune : l'index bénéficie à tous les clients.

### 🧪 TP 8.5 — « Quel index pour quelle requête ? »

*Énoncé.* Pour trois requêtes (une lecture par `GoodId` avec tri, un agrégat par mois sur toute la table, un tableau de bord sur les ventes 2025), choisissez parmi couvrant / filtré / columnstore. Justifiez, créez, mesurez.

<details><summary>Correction</summary>

Lecture ponctuelle triée : couvrant `(GoodId, TradedAt DESC) INCLUDE (...)`. Agrégat sur toute la table : columnstore. Tableau de bord récurrent sur un sous-ensemble : filtré. Un même index ne sert pas les trois ; **chacun se paie en écriture et en espace**.
</details>

---

## 8.6 Lire un plan d'exécution

**Axe concerné : outillage de diagnostic SQL.**

### La notion

Sur de gros volumes, le plan montre **pourquoi** une requête est lente : choix seek/scan, **point de bascule** (*tipping point*) entre *seek + key lookups* et scan, écart estimé/réel, *spills*, parallélisme. Lire le plan est la compétence de base pour comprendre la volumétrie (voir la grille du 00).

### Dans quels cas on est concerné

Dès qu'une requête est lente « sans raison apparente ».

### Exemple : le point de bascule

```csharp
// Même requête, seuils croissants : où le plan passe-t-il de « seek + lookups » à « scan » ?
foreach (var seuil in new[] { 1_000, 20_000, 200_000, 1_000_000 })
    await Mesurer($"GoodId < {seuil:N0}", async db =>
        (await db.Trades.AsNoTracking().Where(t => t.GoodId < seuil).ToListAsync()).Count, afficherSql: false);
```
```sql
SET STATISTICS IO, TIME ON;
SELECT * FROM dbo.Trades WHERE GoodId < 1000;          -- Index Seek (IX_Trades_GoodId) + Key Lookup
SELECT * FROM dbo.Trades WHERE GoodId < 200000;        -- ? Clustered Index Scan (le coût des lookups dépasse celui d'un scan)
```
**À observer** : le plan **réel** de chaque seuil ; les *logical reads* qui **cessent de croître** quand le scan prend le relais ; l'écart *Estimated rows / Actual rows*.

### Lire les avertissements sur plusieurs requêtes

```sql
WITH XMLNAMESPACES (DEFAULT 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
SELECT TOP 20 qs.execution_count, qs.total_logical_reads, SUBSTRING(st.text, 1, 100) AS requete
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE qp.query_plan.exist('//Warnings') = 1 OR qp.query_plan.exist('//RelOp[@PhysicalOp="Key Lookup"]') = 1
ORDER BY qs.total_logical_reads DESC;
```

### Sans index / avec index et procédure stockée

Reprenez la section 6.7 : le plan **change** avec l'index ; vous le constatez ici.

### 🧪 TP 8.6 — « Annoter un plan »

*Énoncé.* Prenez la requête `GoodId < 200000`, imprimez le plan réel et annotez : opérateur d'accès, nombre estimé/réel de lignes, coût relatif de chaque opérateur, présence de lookup, parallélisme. Quel changement ferait **réduire** les lectures ?

<details><summary>Correction</summary>

Si le plan scanne la table parce que les lookups coûteraient plus, un **index couvrant** (`INCLUDE` des colonnes lues) fait redevenir le seek préférable ; ou réduire le nombre de colonnes (projection).
</details>

---

## 8.7 Tables temporelles

**Axe concerné : 🗄️ SQL (historique automatique) + ⚙️ .NET (opérateurs `Temporal*`).**

### La notion

Une **table temporelle** (*system-versioned*) conserve automatiquement l'**historique** des modifications dans une table jumelle. EF Core expose `TemporalAsOf(date)`, `TemporalFromTo`, `TemporalBetween`, `TemporalContainedIn`, `TemporalAll`. Sur de gros volumes : l'**historique grossit** avec les modifications ; il faut le **limiter** (rétention) et l'**indexer**.

### Dans quels cas on est concerné

Audit, « état à une date », analyses de dérive.

### Mise en place

```sql
CREATE TABLE dbo.Lab_Prix (
    Id int NOT NULL PRIMARY KEY, Valeur decimal(18,2) NOT NULL,
    SysDebut datetime2 GENERATED ALWAYS AS ROW START NOT NULL, SysFin datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysDebut, SysFin))
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.Lab_PrixHistorique));
INSERT INTO dbo.Lab_Prix (Id, Valeur) SELECT value, value % 1000 FROM GENERATE_SERIES(1, 100000);
UPDATE dbo.Lab_Prix SET Valeur = Valeur + 1 WHERE Id % 10 = 0;      -- génère de l'historique ; à répéter plusieurs fois, avec une pause
```
```csharp
public class LabPrix { public int Id { get; set; } public decimal Valeur { get; set; } }
// OnModelCreatingPartial : modelBuilder.Entity<LabPrix>(e => { e.ToTable("Lab_Prix", t => t.IsTemporal(p => { p.HasPeriodStart("SysDebut"); p.HasPeriodEnd("SysFin"); p.UseHistoryTable("Lab_PrixHistorique"); })); });
// Contexte : public DbSet<LabPrix> LabPrix => Set<LabPrix>();
```

### Exemple sans l'amélioration

```csharp
// ❌ Reconstituer « l'état d'hier » à la main : requêtes sur deux tables et logique de dates à reproduire
await Mesurer("AVANT : à la main", async db =>
    (await db.Database.SqlQuery<int>($"""
        SELECT COUNT(*) AS Value FROM (
          SELECT Id FROM dbo.Lab_Prix WHERE SysDebut <= {DateTime.UtcNow.AddMinutes(-5)}
          UNION ALL SELECT Id FROM dbo.Lab_PrixHistorique WHERE SysDebut <= {DateTime.UtcNow.AddMinutes(-5)} AND SysFin > {DateTime.UtcNow.AddMinutes(-5)}) x
        """).ToListAsync()).Single());
```

### Exemple avec l'amélioration

```csharp
// ✔ L'opérateur AsOf : EF et SQL Server font le travail
var instant = DateTime.UtcNow.AddMinutes(-5);
await Mesurer("APRÈS : TemporalAsOf", async db => await db.LabPrix.TemporalAsOf(instant).CountAsync(), afficherSql: true);
await Mesurer("TemporalBetween", async db => await db.LabPrix.TemporalBetween(instant.AddHours(-1), instant).CountAsync(), afficherSql: true);
```
**À observer** : `FOR SYSTEM_TIME AS OF @p` dans le SQL ; la **taille de l'historique** (`SELECT COUNT(*) FROM dbo.Lab_PrixHistorique`) après plusieurs vagues de mises à jour ; le plan sur la table d'historique (son index clusterisé est sur `(SysFin, SysDebut)`).

### Sans index / avec index

```sql
CREATE INDEX IX_Lab_PrixHist_Id ON dbo.Lab_PrixHistorique (Id, SysFin, SysDebut);
-- utile pour « l'historique d'une entité » (WHERE Id = x) ; sans lui, scan de l'historique
```
Gardez l'historique sous contrôle : `ALTER TABLE dbo.Lab_Prix SET (SYSTEM_VERSIONING = ON (HISTORY_RETENTION_PERIOD = 7 DAYS));`.

### Comparaison avec une procédure stockée

Un **déclencheur** + une table d'audit écrite à la main est l'alternative historique : plus de code, mais contrôle total. La table temporelle est **plus simple et plus sûre** (aucune écriture possible dans l'historique).

### 🧪 TP 8.7 — « L'historique d'un prix »

*Énoncé.* Retournez l'évolution de la valeur de l'`Id = 10` sur les dernières 24 heures avec `TemporalAll`. Comparez les lectures avant/après l'index `IX_Lab_PrixHist_Id`.

<details><summary>Correction</summary>

```csharp
db.LabPrix.TemporalAll().Where(p => p.Id == 10).OrderBy(p => EF.Property<DateTime>(p, "SysDebut")).Select(p => new { p.Valeur, Debut = EF.Property<DateTime>(p, "SysDebut"), Fin = EF.Property<DateTime>(p, "SysFin") })
```
Sans index sur `(Id, …)` dans l'historique, la recherche par `Id` scanne l'historique ; avec lui, seek.
</details>

---

## 8.8 `COUNT(*)` coûteux

**Axe concerné : 🗄️ SQL (parcours complet).** *Base : 7.3.3.*

### La notion

Un `COUNT(*)` exact lit toute la table (ou le plus petit index). Pour un **compteur affiché souvent**, trois approches : **métadonnées** (`sys.dm_db_partition_stats`), **vue indexée** avec `COUNT_BIG(*)`, **compteur incrémenté** à l'écriture.

### Dans quels cas on est concerné

Tableaux de bord, en-têtes de liste, quotas.

### Exemple sans l'amélioration

```csharp
await Mesurer("AVANT : Count exact par bien", db => db.Trades.CountAsync(t => t.GoodId == 12_345), afficherSql: true);       // seek rapide (FK indexée)
await Mesurer("AVANT : Count global", db => db.Trades.CountAsync(), afficherSql: true);
```

### Exemple avec l'amélioration

```sql
CREATE VIEW dbo.Lab_vwNbEchangesParBien WITH SCHEMABINDING AS
SELECT GoodId, COUNT_BIG(*) AS N FROM dbo.Trades GROUP BY GoodId;
GO
CREATE UNIQUE CLUSTERED INDEX IX_Lab_vwNb ON dbo.Lab_vwNbEchangesParBien (GoodId);
```
```csharp
await Mesurer("APRÈS : métadonnées", async db =>
    (await db.Database.SqlQuery<long>($"SELECT SUM(row_count) AS Value FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('dbo.Trades') AND index_id IN (0,1)").ToListAsync()).Single());
await Mesurer("APRÈS : vue indexée (total)", async db =>
    (await db.Database.SqlQuery<long>($"SELECT SUM(N) AS Value FROM dbo.Lab_vwNbEchangesParBien WITH (NOEXPAND)").ToListAsync()).Single());
```
**À observer** : *logical reads* du `COUNT(*)` global (toute la table ou le plus petit index) contre les métadonnées (quelques pages) et la vue (1 M de lignes agrégées au plus). Le **comptage par bien** reste rapide grâce à l'index de FK.

### Sans index / avec index

Un `COUNT(*)` sans filtre lit déjà le **plus petit index** (par exemple `IX_Trades_GoodId`) : c'est le seul « index » qui l'aide ; il ne le rend pas constant.

### Comparaison avec une procédure stockée

Une procédure ne change rien ; le **compteur incrémenté** (table `Compteurs`, mise à jour à chaque écriture dans la même transaction) rend la lecture instantanée au prix d'une contention sur la ligne du compteur.

### 🧪 TP 8.8 — « Quatre façons de compter »

*Énoncé.* Tableau : exact / métadonnées / vue indexée / compteur incrémenté → exactitude, coût de lecture, coût d'écriture, contention.

<details><summary>Correction</summary>

Exact : exact, lecture coûteuse, écriture nulle. Métadonnées : approchées (parfois en retard), lecture instantanée, écriture nulle. Vue indexée : exacte, lecture quasi instantanée, écriture plus lente (maintenance), contention possible sur les groupes populaires. Compteur : exact, lecture instantanée, écriture plus lente, **forte contention** sur une ligne unique.
</details>

---

## 8.9 Agrégats lourds

**Axe concerné : 🗄️ SQL (pré-calcul). ** *Base : 4.7.*

### La notion

Un agrégat récurrent sur des millions de lignes se **pré-calcule** : **vue indexée** (maintenue automatiquement), **table de pré-agrégation** (alimentée par lots, ex. quotidiennement), **columnstore** (8.5). Le choix dépend de la **fraîcheur** exigée et du coût d'**écriture** admissible.

### Dans quels cas on est concerné

Rapports mensuels, tableaux de bord, séries temporelles.

### Exemple sans l'amélioration

```csharp
// ❌ Volume mensuel par bien, recalculé à chaque affichage
await Mesurer("AVANT : à la volée", async db =>
    (await db.Trades.Where(t => t.TradedAt >= new DateTime(2025, 1, 1) && t.GoodId <= 10_000)
        .GroupBy(t => new { t.GoodId, Mois = t.TradedAt.Month }).Select(g => new { g.Key.GoodId, g.Key.Mois, Q = g.Sum(t => (long)t.Quantity) }).ToListAsync()).Count, afficherSql: true);
```

### Exemple avec l'amélioration

```sql
-- ✔ Table de pré-agrégation, alimentée par lot
CREATE TABLE dbo.Lab_Rollup (GoodId int NOT NULL, Annee int NOT NULL, Mois int NOT NULL, Quantite bigint NOT NULL, PRIMARY KEY (GoodId, Annee, Mois));
INSERT INTO dbo.Lab_Rollup SELECT GoodId, YEAR(TradedAt), MONTH(TradedAt), SUM(CAST(Quantity AS bigint)) FROM dbo.Trades GROUP BY GoodId, YEAR(TradedAt), MONTH(TradedAt);
```
```csharp
public class LabRollup { public int GoodId { get; set; } public int Annee { get; set; } public int Mois { get; set; } public long Quantite { get; set; } }
// OnModelCreatingPartial : modelBuilder.Entity<LabRollup>().ToTable("Lab_Rollup").HasKey(r => new { r.GoodId, r.Annee, r.Mois });
await Mesurer("APRÈS : table de pré-agrégation", async db =>
    (await db.LabRollups.AsNoTracking().Where(r => r.Annee == 2025 && r.GoodId <= 10_000).ToListAsync()).Count, afficherSql: true);
```
**À observer** : lectures logiques (6 M de lignes lues contre quelques milliers) ; le **coût de la construction** (une seule fois) ; la **fraîcheur** : les nouvelles lignes de `Trades` n'apparaissent dans le rollup qu'après sa prochaine alimentation.

### Sans index / avec index

La clé primaire `(GoodId, Annee, Mois)` sert la lecture par bien et par année. Pour un rollup lu sur `Annee` seul, un index `(Annee, GoodId)` serait préférable.

### Comparaison avec une procédure stockée

Une **procédure d'alimentation** (appelée par un job) met à jour le rollup de manière incrémentale (`MERGE` sur la dernière période) : c'est le cas **typique** où la procédure est préférable à LINQ (traitement ensembliste planifié).

### 🧪 TP 8.9 — « Choisir la stratégie »

*Énoncé.* Pour 3 besoins (tableau de bord temps réel, rapport quotidien, rapport mensuel d'audit), choisissez entre vue indexée, rollup et agrégat à la volée. Justifiez avec fraîcheur, coût d'écriture, complexité.

<details><summary>Correction</summary>

Temps réel : vue indexée (si la table est peu écrite) ou columnstore ; rapport quotidien : rollup alimenté la nuit ; audit mensuel rare : agrégat à la volée (pas de structure à maintenir).
</details>

---

## 8.10 Tri, `Distinct` et `GroupBy` volumineux

**Axe concerné : 🗄️ SQL (mémoire serveur, `tempdb`).**

### La notion

Tri, hachage (`Hash Match`), regroupement demandent de la **mémoire** (allouée avant l'exécution : *memory grant*). Si l'estimation est trop basse ou la mémoire insuffisante, l'opérateur **déborde sur `tempdb`** (*spill*) : la requête devient beaucoup plus lente. Remèdes : **fournir l'ordre** par un index, **réduire les colonnes** triées, borner (`TOP`), corriger les estimations (statistiques), réduire le volume.

### Dans quels cas on est concerné

`OrderBy` sur de gros ensembles non indexés, `Distinct`/`GroupBy` à forte cardinalité.

### Exemple sans l'amélioration

On **provoque** un débordement en limitant la mémoire accordée (intercepteur du 7.5.1, tag `grantpetit` → `OPTION (MAX_GRANT_PERCENT = 0.1)` à ajouter à l'intercepteur) :

```csharp
await Mesurer("AVANT : tri large avec peu de mémoire", async db =>
    (await db.Trades.TagWith("grantpetit").AsNoTracking().OrderBy(t => t.UnitPrice).ThenBy(t => t.Id).Take(1_000_000).Select(t => t.Id).ToListAsync()).Count, afficherSql: true);
```
Dans le plan réel : un avertissement **« Sort Warning / Spill to tempdb »** sur l'opérateur `Sort` ; côté serveur :

```sql
SELECT session_id, internal_objects_alloc_page_count FROM sys.dm_db_task_space_usage WHERE internal_objects_alloc_page_count > 0;   -- pendant l'exécution
```

### Exemple avec l'amélioration

```sql
-- ✔ L'index fournit l'ordre : plus de tri
CREATE INDEX IX_Lab_Trades_Price_Id ON dbo.Trades (UnitPrice, Id);
```
```csharp
// relancer la même requête (sans le tag) : le plan lit l'index dans l'ordre, s'arrête à 1 000 000 lignes
await Mesurer("APRÈS : tri fourni par l'index", async db =>
    (await db.Trades.AsNoTracking().OrderBy(t => t.UnitPrice).ThenBy(t => t.Id).Take(1_000_000).Select(t => t.Id).ToListAsync()).Count, afficherSql: true);
```
**Variante `Distinct`** : `db.Trades.Select(t => new { t.GoodId, t.CompanyId }).Distinct().CountAsync()` avec et sans le tag `grantpetit` : un `Hash Match` qui déborde ; et avec un index `(GoodId, CompanyId)` : un `Stream Aggregate`.

**À observer** : l'avertissement de *spill* ; la durée ; `tempdb` ; *logical reads* + **écritures** (`logical writes` dans `STATISTICS IO` = signe de débordement).

### Sans index / avec index

L'index `(UnitPrice, Id)` supprime le tri et son débordement ; il coûte à l'écriture (nouvel index sur 6 M de lignes).

### Comparaison avec une procédure stockée

Aucune différence de plan ; en procédure on peut utiliser une **table temporaire** avec index pour structurer un traitement en plusieurs étapes (contrôle fin de la mémoire).

### 🧪 TP 8.10 — « Dessiner un spill »

*Énoncé.* Reproduisez un spill avec le tag, relevez l'avertissement, la durée, les écritures. Supprimez le tag et notez la différence. Proposez trois remèdes **sans** augmenter la mémoire du serveur.

<details><summary>Correction</summary>

Remèdes : (1) index qui fournit l'ordre ; (2) ne trier que les colonnes utiles puis rejoindre pour récupérer le reste (tri sur `Id` + `UnitPrice` seulement) ; (3) paginer/limiter le tri (`TOP N` avec `Take` raisonnable).
</details>

---

## 8.11 Mémoire du client

**Axe concerné : 🧠 mémoire .NET. ** *Base : 4.3.*

### La notion

Chaque entité suivie coûte **plusieurs fois** la taille de ses données (objet + instantané + entrée de suivi + références). Un million d'entités suivies pèse **des centaines de Mo** ; sans suivi, environ la taille des objets ; en projection légère, encore moins. Connaître le **coût par entité** permet de dimensionner les lots.

### Dans quels cas on est concerné

Choisir une taille de lot, prévoir la mémoire d'un service.

### Exemple (mesure du coût par entité)

```csharp
foreach (var n in new[] { 100_000, 500_000, 1_000_000 })
{
    await Mesurer($"suivi {n:N0}", async db => { var l = await db.Goods.Where(g => g.Id <= n).ToListAsync(); return $"{(GC.GetTotalMemory(true) / (double)l.Count):N0} octets/entité"; });
    await Mesurer($"sans suivi {n:N0}", async db => { var l = await db.Goods.AsNoTracking().Where(g => g.Id <= n).ToListAsync(); return $"{(GC.GetTotalMemory(true) / (double)l.Count):N0} octets/entité"; });
    await Mesurer($"projection {n:N0}", async db => { var l = await db.Goods.Where(g => g.Id <= n).Select(g => new { g.Id, g.BasePrice }).ToListAsync(); return $"{(GC.GetTotalMemory(true) / (double)l.Count):N0} octets/ligne"; });
}
```
**À observer** : « octets par entité » dans les trois cas ; l'évolution du **pic** (échantillonneur du 00) ; `Gen 2 GC Count`.

### Sans index / avec index et procédure stockée

Sans objet.

### 🧪 TP 8.11 — « Dimensionner un lot »

*Énoncé.* Votre service dispose de 500 Mo pour un traitement par lots d'entités suivies. À partir du coût par entité mesuré, déterminez la taille de lot maximale et validez par une exécution.

<details><summary>Correction</summary>

Taille de lot ≈ budget / (coût par entité × marge de sécurité 2) : le GC peut retenir temporairement plus que la taille utile. Validez en regardant le pic réel (`GC Heap Size`).
</details>

---

## 8.12 Lecture parallèle

**Axe concerné : ⚙️ .NET + 🗄️ SQL (concurrence).**

### La notion

Un `DbContext` n'est **pas thread-safe** : une seule opération à la fois. Pour paralléliser une lecture de grande table, on **partitionne** par plages de clé et on donne **un contexte par tâche** (`IDbContextFactory<T>`). Gain possible quand le goulot est la **latence/CPU client** ; faible quand le goulot est le disque/CPU **serveur** (les requêtes se disputent les mêmes ressources).

### Dans quels cas on est concerné

Traitements batch lourds en CPU, lecture d'énormes tables vers des services distants.

### Exemple sans l'amélioration

```csharp
// ❌ Un seul contexte partagé par plusieurs tâches : exception
await Mesurer("AVANT : contexte partagé", async db =>
{
    try { await Task.WhenAll(Enumerable.Range(0, 4).Select(i => db.Goods.CountAsync(g => g.Id % 4 == i))); return "ok"; }
    catch (Exception e) { return e.GetType().Name + " : " + e.Message.Split('.')[0]; }
});

// Séquentiel (référence)
await Mesurer("Séquentiel", async db =>
{ long s = 0; for (var i = 0; i < 4; i++) s += await db.Goods.Where(g => g.Id > i * 250_000 && g.Id <= (i + 1) * 250_000).SumAsync(g => (long)g.WeightKg); return s; });
```

### Exemple avec l'amélioration

```csharp
// ✔ Un contexte par tâche, une plage de clés par tâche
using var sp = new ServiceCollection().AddPooledDbContextFactory<StarWarsContext>(o => o.UseSqlServer(Cs), poolSize: 16).BuildServiceProvider();
var fabrique = sp.GetRequiredService<IDbContextFactory<StarWarsContext>>();

await Mesurer("APRÈS : 4 contextes en parallèle", async _ =>
{
    long total = 0;
    await Parallel.ForEachAsync(Enumerable.Range(0, 4), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
    {
        await using var c = await fabrique.CreateDbContextAsync(ct);
        var part = await c.Goods.Where(g => g.Id > i * 250_000 && g.Id <= (i + 1) * 250_000).SumAsync(g => (long)g.WeightKg, ct);
        Interlocked.Add(ref total, part);
    });
    return total;
});
```
**À observer** : l'exception du contexte partagé (*« A second operation was started on this context instance… »*) ; le temps séquentiel contre parallèle ; côté SQL, le nombre de sessions actives (`sys.dm_exec_requests`) et le **CPU serveur** (si le serveur est saturé, le gain disparaît) ; pensez à la **limite du pool de connexions** (100 par défaut).

### Sans index / avec index

Les plages sur la clé primaire sont des *seeks* de plage : aucun index à ajouter.

### Comparaison avec une procédure stockée

Le parallélisme **dans** SQL Server (`MAXDOP`) est géré par le moteur ; le parallélisme client ajoute des sessions concurrentes. Ne cumulez pas sans mesurer.

### 🧪 TP 8.12 — « Quel degré de parallélisme ? »

*Énoncé.* Mesurez le temps pour 1, 2, 4, 8, 16 tâches. Tracez. À partir de quand le gain cesse-t-il ? Quel indicateur côté SQL vous le dit ?

<details><summary>Correction</summary>

Le gain plafonne quand le **serveur** (CPU, disque) devient le facteur limitant ou que le pool de connexions est saturé ; les attentes `CXPACKET`/`PAGEIOLATCH`, le CPU serveur et la file d'attente du pool le montrent.
</details>

---

## 8.13 Timeouts, annulation et verrous

**Axe concerné : 🗄️ SQL (blocages). ** *Base : 7.5.3.*

### La notion

Sur de gros volumes, une écriture longue **bloque les lecteurs** (niveau d'isolation par défaut : `READ COMMITTED` avec verrous). Le lecteur attend, puis expire (`CommandTimeout`). On observe les verrous (`sys.dm_tran_locks`) et les attentes (`sys.dm_os_waiting_tasks`).

### Dans quels cas on est concerné

Mise à jour batch pendant la journée, rapports en lecture sur une table très écrite.

### Exemple (deux sessions)

```csharp
await using var ecrivain = NewDb(); await using var lecteur = NewDb();
await using var tx = await ecrivain.Database.BeginTransactionAsync();
await ecrivain.Goods.Where(g => g.Id <= 300_000).ExecuteUpdateAsync(s => s.SetProperty(g => g.WeightKg, g => g.WeightKg + 1));      // verrous détenus, transaction ouverte

lecteur.Database.SetCommandTimeout(5);
var sw = Stopwatch.StartNew();
try { await lecteur.Goods.CountAsync(g => g.Id == 12_345); Console.WriteLine($"lu en {sw.ElapsedMilliseconds} ms"); }
catch (Exception e) { Console.WriteLine($"bloqué : {e.GetType().Name} après {sw.ElapsedMilliseconds} ms"); }

await tx.RollbackAsync();
```
```sql
-- Pendant le blocage, dans un autre terminal :
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time FROM sys.dm_exec_requests r WHERE r.blocking_session_id <> 0;
SELECT resource_type, request_mode, request_status, COUNT(*) FROM sys.dm_tran_locks GROUP BY resource_type, request_mode, request_status;
```
**À observer** : le lecteur est **bloqué** (verrou de table ou de page selon l'escalade, voir 8.2) ou **passe** (si les lignes lues ne sont pas verrouillées) ; `blocking_session_id` ; l'attente `LCK_M_S`.

### Sans index / avec index

Un index qui permet de **ne pas lire** les lignes verrouillées évite le blocage : ici, le `COUNT` par `Id` lit une ligne **non verrouillée si le verrou est de ligne**, et se bloque si le verrou est de table.

### Comparaison avec une procédure stockée

Aucune : le verrouillage dépend de la transaction, pas du véhicule.

### 🧪 TP 8.13 — « Qui bloque qui ? »

*Énoncé.* Reproduisez un blocage, identifiez la session bloquante et la requête qu'elle exécute (`sys.dm_exec_sql_text`), et proposez deux remèdes.

<details><summary>Correction</summary>

Remèdes : lots plus petits (8.2) pour éviter l'escalade ; niveau d'isolation avec **versionnement des lignes** (`READ_COMMITTED_SNAPSHOT`, 8.14) pour que les lecteurs ne soient pas bloqués par les écrivains.
</details>

---

## 8.14 Niveaux d'isolation

**Axe concerné : 🗄️ SQL (concurrence) + justesse.** *Base : 3.9.*

### La notion

| Niveau | Les lecteurs… | Risque |
|---|---|---|
| `READ COMMITTED` (défaut) | sont **bloqués** par les écrivains | blocages |
| `READ COMMITTED SNAPSHOT` (RCSI, option de base) | lisent la **dernière version validée** sans bloquer | coût `tempdb` (versions) |
| `SNAPSHOT` | lisent une **image cohérente** au début de la transaction | idem, + conflits d'écriture |
| `READ UNCOMMITTED` / `NOLOCK` | ne sont jamais bloqués | **lectures sales**, lignes lues deux fois ou ratées |

### Dans quels cas on est concerné

Rapports sur une base transactionnelle ; lectures longues pendant des écritures batch.

### Exemple sans l'amélioration

```csharp
// ⚠ READ UNCOMMITTED (équivalent de NOLOCK) « pour que ça passe » : lecture sale
await using var e = NewDb(); await using var l = NewDb();
await using var tx = await e.Database.BeginTransactionAsync();
await e.Goods.Where(g => g.Id == 42).ExecuteUpdateAsync(s => s.SetProperty(g => g.BasePrice, 999_999m));      // non validé
await using var txl = await l.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted);
Console.WriteLine($"lecture sale : {(await l.Goods.AsNoTracking().Where(g => g.Id == 42).Select(g => g.BasePrice).FirstAsync())}");   // 999999 : valeur jamais validée
await tx.RollbackAsync();
```

### Exemple avec l'amélioration

```sql
-- ✔ RCSI : option de base (change le comportement de TOUTES les lectures READ COMMITTED) — à désactiver après le test
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
```
Relancez le scénario 8.13 : le lecteur **n'est plus bloqué** et lit la **dernière valeur validée** (la valeur d'avant la mise à jour). Puis :

```sql
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;
```
**À observer** : l'absence d'attente ; `tempdb` (magasin de versions : `sys.dm_tran_version_store_space_usage`) ; la valeur lue (ancienne, **cohérente**) contre la lecture sale (non validée).

### Sans index / avec index

Sans objet. (Le versionnement augmente la taille des lignes de 14 octets et l'utilisation de `tempdb`.)

### Comparaison avec une procédure stockée

Un niveau d'isolation se fixe dans la **session** ou la transaction, y compris dans une procédure : `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`.

### 🧪 TP 8.14 — « Choisir l'isolation d'un rapport »

*Énoncé.* Un rapport de 30 secondes lit `Goods` pendant qu'un traitement batch la met à jour par lots. Comparez : `READ COMMITTED`, RCSI, `SNAPSHOT`, `NOLOCK`. Pour chacun : le rapport est-il bloqué ? Est-il cohérent ?

<details><summary>Correction</summary>

`READ COMMITTED` : bloqué par instants, cohérent par ligne ; RCSI : jamais bloqué, cohérent **par instruction** ; `SNAPSHOT` : jamais bloqué, cohérent pour **toute la transaction** ; `NOLOCK` : jamais bloqué, **peut lire des données non validées ou en double**. Pour un rapport : `SNAPSHOT` ou RCSI ; `NOLOCK` seulement s'il est acceptable que le chiffre soit faux.
</details>

---

## 8.15 Lecture incrémentale

**Axe concerné : 🗄️ SQL (ne lire que ce qui a changé).**

### La notion

Pour synchroniser un système externe, on ne relit pas toute la table : on lit les lignes **modifiées depuis la dernière synchronisation**, avec une colonne **de version** (`UpdatedAt`, `rowversion`). `MarketGoods.UpdatedAt` (`datetime2`) existe déjà. Pièges : plusieurs lignes avec le **même** horodatage à la limite du lot (curseur sur `(UpdatedAt, clé)`), horloges non monotones, mises à jour **non horodatées**.

### Dans quels cas on est concerné

Synchronisation, indexation dans un moteur de recherche, alimentation d'un entrepôt.

### Exemple sans l'amélioration

```csharp
var depuis = new DateTime(2025, 12, 1);
// ❌ Tout relire et comparer côté client
await Mesurer("AVANT : tout relire", async db =>
    (await db.MarketGoods.AsNoTracking().ToListAsync()).Count(m => m.UpdatedAt > depuis));      // 4 M de lignes
```

### Exemple avec l'amélioration

```csharp
// ✔ Filtre sur la colonne de version
await Mesurer("APRÈS : UpdatedAt >", async db =>
    (await db.MarketGoods.AsNoTracking().Where(m => m.UpdatedAt > depuis).OrderBy(m => m.UpdatedAt).ThenBy(m => m.MarketId).ThenBy(m => m.GoodId).Take(10_000).ToListAsync()).Count, afficherSql: true);
```
**À observer** : sans index sur `UpdatedAt`, la requête **scanne** 4 M de lignes même pour 10 000 résultats ; avec index, seek.

### Sans index / avec index

```sql
SET STATISTICS IO, TIME ON;
CREATE INDEX IX_Lab_MarketGoods_UpdatedAt ON dbo.MarketGoods (UpdatedAt) INCLUDE (Stock, Price);
-- relancer : Seek sur la plage ; plus de scan
DROP INDEX IX_Lab_MarketGoods_UpdatedAt ON dbo.MarketGoods;
```
**Curseur robuste** : `Where(m => m.UpdatedAt > dernierDate || (m.UpdatedAt == dernierDate && (m.MarketId > dernierMarche || (m.MarketId == dernierMarche && m.GoodId > dernierBien))))` ; et **chevauchement** : relire à partir de `dernierDate - 1 minute` et dédoublonner côté consommateur pour absorber les transactions en cours de validation.

### Comparaison avec une procédure stockée / `rowversion`

```sql
CREATE OR ALTER PROCEDURE dbo.Lab_Changements @Depuis datetime2, @Taille int
AS BEGIN SET NOCOUNT ON;
  SELECT TOP (@Taille) MarketId, GoodId, Stock, Price, UpdatedAt FROM dbo.MarketGoods WHERE UpdatedAt > @Depuis ORDER BY UpdatedAt, MarketId, GoodId; END
```
Même plan que le LINQ **avec** l'index. Une colonne **`rowversion`** (valeur monotone par base, mise à jour automatiquement à chaque écriture) évite les pièges d'horloge : préférez-la quand vous pouvez ajouter la colonne.

### 🧪 TP 8.15 — « Synchroniser sans rien perdre »

*Énoncé.* Écrivez une boucle de synchronisation qui lit par lots de 10 000 les lignes `UpdatedAt > curseur` avec le curseur composite, persiste le curseur, et gère le chevauchement d'une minute. Montrez qu'aucune ligne n'est perdue quand deux lignes ont exactement le même horodatage.

<details><summary>Correction</summary>

Simulez en créant deux lignes de même `UpdatedAt` (dans une transaction annulée) à cheval sur la limite d'un lot : avec un simple `UpdatedAt > dernier`, la seconde est **perdue** ; avec le curseur `(UpdatedAt, MarketId, GoodId)`, elle est lue au lot suivant.
</details>

---

## Script de nettoyage

```sql
DROP INDEX IF EXISTS IX_Lab_Trades_Cover          ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_Ventes2025     ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Trades_CS             ON dbo.Trades;      -- columnstore
DROP INDEX IF EXISTS IX_Lab_Trades_Price_Id       ON dbo.Trades;
DROP INDEX IF EXISTS IX_Lab_Item_Valeur           ON dbo.Lab_Item;
DROP INDEX IF EXISTS IX_Lab_MarketGoods_UpdatedAt ON dbo.MarketGoods;

DROP VIEW IF EXISTS dbo.Lab_vwNbEchangesParBien;                      -- supprime aussi son index clusterisé

DROP PROCEDURE IF EXISTS dbo.Lab_MajParLots;
DROP PROCEDURE IF EXISTS dbo.Lab_Changements;

-- Table temporelle : désactiver le versionnement avant de supprimer
IF OBJECT_ID('dbo.Lab_Prix') IS NOT NULL ALTER TABLE dbo.Lab_Prix SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS dbo.Lab_Prix;
DROP TABLE IF EXISTS dbo.Lab_PrixHistorique;

DROP TABLE IF EXISTS dbo.Lab_Rollup;
DROP TABLE IF EXISTS dbo.Lab_Item;

-- Option de base modifiée en 8.14 : doit être OFF
ALTER DATABASE StarWarsTrade SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;

-- Vérification : ne doit rien retourner (la dernière requête doit donner 0)
SELECT name FROM sys.indexes WHERE name LIKE 'IX[_]Lab%';
SELECT name FROM sys.objects WHERE name LIKE 'Lab[_]%';
SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = 'StarWarsTrade';
```
