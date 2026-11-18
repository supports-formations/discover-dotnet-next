# 11 — Mesure et diagnostic : les outils à connaître

> Suite de [`10-hors-linq.md`](10-hors-linq.md). Ce chapitre **approfondit les outils** que le [00](00-prerequis.md) a présentés : à quoi sert chacun, **ce qu'il révèle**, **ce qu'il coûte**, comment l'utiliser **en développement** et **en production**.
> Aucun code n'est créé hors de ce document ; aucun chiffre n'est annoncé. **Sources** : les catégories de journal, les compteurs/métriques EF Core et l'API `LogTo` ont été **recoupés avec la documentation officielle** ; ce qui ne l'est pas est marqué *(non recoupé)*.
> Pour les outils, la rubrique « sans l'amélioration » montre **ce qu'on fait sans l'outil** (deviner) et « avec » **ce que l'outil apporte**. Les rubriques « index » et « procédure stockée » indiquent ce que l'outil permet de **vérifier** sur ces sujets.
> Objets de démonstration : préfixes `Lab_` / `lab_`. **Script de nettoyage** en fin de fichier.
> Les extraits qui utilisent `db` en dehors de `Mesurer(...)` supposent `await using var db = NewDb();`.

## Sommaire

| # | Outil | Question à laquelle il répond |
|---|---|---|
| [11.1](#111-toquerystring) | `ToQueryString()` | Quel SQL **sera** envoyé ? |
| [11.2](#112-journal-des-commandes-logto--ilogger) | `LogTo` / `ILogger` | Quel SQL **est parti**, avec quelles valeurs et quelle durée ? |
| [11.3](#113-intercepteurs-de-commandes) | `DbCommandInterceptor` | Comment **compter, mesurer, bloquer** les commandes ? |
| [11.4](#114-compteurs-et-métriques-ef-core) | Compteurs et métriques EF Core | Que fait l'application **en continu** ? |
| [11.5](#115-opentelemetry-et-traces) | OpenTelemetry | Comment **exporter** ces mesures ? |
| [11.6](#116-benchmarkdotnet) | BenchmarkDotNet | Quel est le coût **CPU/allocations** d'un code ? |
| [11.7](#117-set-statistics-io--time-et-statistiques-de-session) | `SET STATISTICS IO/TIME`, statistiques de session | Combien de **pages lues**, de CPU serveur ? |
| [11.8](#118-plan-dexécution-réel) | Plan d'exécution réel | **Pourquoi** SQL Server fait ce qu'il fait ? |
| [11.9](#119-query-store-dmv-et-extended-events) | Query Store, DMV, Extended Events | Qu'est-ce qui est lent **en production**, et **depuis quand** ? |
| [11.10](#1110-tests-dintégration-et-garde-fous) | Tests d'intégration + garde-fous | Comment **empêcher** la régression ? |

---

## 11.1 `ToQueryString()`

**Axe : outillage — 🗄️ SQL (avant exécution).**

### Ce que c'est

`IQueryable.ToQueryString()` retourne le SQL (avec les **déclarations de paramètres**) **sans l'envoyer**. C'est le moyen le plus rapide de **vérifier une traduction** : présence d'un `WHERE`, `OR … IS NULL`, jointures, colonnes.

### Dans quels cas

Écriture d'une requête, revue de code, tests unitaires de requêtes, diagnostic d'une requête « bizarre ».

### Sans l'outil

```csharp
var l = await db.Planets.Where(p => p.Markets.Any(m => m.IsBlackMarket) && p.Population > 1_000_000).ToListAsync();     // « ça marche »
```

### Avec l'outil

```csharp
var q = db.Planets.Where(p => p.Markets.Any(m => m.IsBlackMarket) && p.Population > 1_000_000);
Console.WriteLine(q.ToQueryString());
```
**Ce qu'on lit** : `EXISTS` ; `WHERE` complet ; liste des colonnes (toutes → projection manquante) ; `@__p_0` pour une valeur capturée.

**Limites** : il montre ce que **EF générerait**, pas ce qui **est** parti (ex. un intercepteur qui modifie le texte — 7.5.1 — n'y apparaît pas) ; il ne donne ni durée ni plan ; il **ne fonctionne pas** pour les requêtes qui ne sont pas `IQueryable` (`ExecuteUpdate`, agrégats terminaux comme `CountAsync`) : écrivez la requête **sans** le terminal (`q.ToQueryString()` avant `CountAsync`).

### Sans index / avec index et procédure stockée

Il révèle si un **index** pourrait servir (prédicat sargable ou non, tri) ; pour une procédure appelée par `FromSql`, il montre le SQL **enveloppé**.

### 🧪 TP 11.1 — « Un test qui lit le SQL »

*Énoncé.* Écrivez un test automatisé qui échoue si la requête « biens de contrebande au-dessus de 90 000 » ne contient pas `WHERE` ou ramène plus de 5 colonnes.

<details><summary>Correction</summary>

```csharp
var sql = db.Goods.Where(g => g.IsContraband && g.BasePrice > 90_000).Select(g => new { g.Id, g.Sku }).ToQueryString();
Assert(sql.Contains("WHERE") && sql.Split("SELECT")[1].Split("FROM")[0].Count(c => c == ',') < 5);
```
C'est un garde-fou **économique** (aucune base requise pour générer le SQL), mais il vérifie le **texte**, pas les performances.
</details>

---

## 11.2 Journal des commandes (`LogTo` / `ILogger`)

**Axe : outillage — 🗄️ SQL (après envoi).**

### Ce que c'est

`LogTo(Action<string>, categories, niveau)` (ou `ILogger` via `UseLoggerFactory`) affiche chaque **commande envoyée**, avec **durée** et paramètres. Les messages sont rangés par **catégories hiérarchiques** (recoupé) :

| Catégorie | Contenu |
|---|---|
| `Microsoft.EntityFrameworkCore.Database.Command` | commandes SQL |
| `…Database.Connection` / `…Database.Transaction` | connexions / transactions |
| `…Query` | requêtes (hors base) — avertissements de requêtes |
| `…ChangeTracking` | suivi des changements |
| `…Update` | enregistrement des entités |
| `…Migrations` | migrations |

`EnableSensitiveDataLogging()` ajoute les **valeurs** des paramètres (désactivé par défaut) ; `EnableDetailedErrors()` conserve plus d'information dans les erreurs de lecture.

### Dans quels cas

Développement (voir le SQL réel), diagnostic en production **ciblé** (catégorie `Database.Command`, niveau `Information`).

### Sans l'outil

```csharp
// On devine la requête envoyée à partir du LINQ…
```

### Avec l'outil

```csharp
var b = new DbContextOptionsBuilder<StarWarsContext>().UseSqlServer(Cs)
    .LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information);
// valeurs des paramètres : .EnableSensitiveDataLogging()   (jamais en production)
```
**Production** (pratique recommandée par la documentation) : un **`ILoggerFactory` partagé** et un filtre précis ; **ne créez pas une fabrique par requête** (coût et fuite) :

```csharp
static readonly ILoggerFactory Fabrique = LoggerFactory.Create(b => b
    .AddFilter((cat, niveau) => cat == DbLoggerCategory.Database.Command.Name && niveau == LogLevel.Information).AddConsole());
// .UseLoggerFactory(Fabrique)
```
**Coût** : écrire le texte de chaque commande (E/S console/fichier) peut **dominer** une requête rapide : mesurez vos performances **sans** journal. Les **avertissements** (ex. plusieurs `Include` de collections, `Take` sans `OrderBy`) sont émis aux niveaux `Warning` de la catégorie `Query`.

### Sans index / avec index et procédure stockée

Le journal donne la **durée par commande** : comparez la durée d'une requête avant/après un index, ou d'un appel `EXEC proc` contre sa version LINQ.

### 🧪 TP 11.2 — « Mesurer le coût du journal »

*Énoncé.* Exécutez 2 000 lectures par clé avec journal console, avec journal désactivé, avec journal filtré sur un niveau non émis. Quel est le surcoût ?

<details><summary>Correction</summary>

Avec console, le coût d'écriture domine (temps multiplié) ; avec un filtre qui rejette tout au niveau de la **fabrique**, le surcoût est faible mais non nul. Conclusion : en production, journalisez ce qui est **utile et filtrable** (commandes lentes), pas tout.
</details>

---

## 11.3 Intercepteurs de commandes

**Axe : outillage — ⚙️ .NET + 🗄️ SQL.**

### Ce que c'est

Un `DbCommandInterceptor` s'exécute **avant/après** chaque commande : on peut **compter**, **chronométrer**, **détecter les requêtes lentes**, **bloquer** (interdire), **réécrire** (hints, 7.5.1). Le `CompteurSql` du 00 en est un exemple.

### Dans quels cas

Compter les requêtes par requête HTTP (détection N+1), alerter sur les commandes lentes, imposer des règles.

### Sans l'outil

```csharp
// On ne sait pas combien de requêtes un appel émet réellement
```

### Avec l'outil

```csharp
public sealed class DetecteurLenteur(TimeSpan seuil, Action<string, TimeSpan> alerte) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand c, CommandExecutedEventData e, DbDataReader r)
    { if (e.Duration > seuil) alerte(c.CommandText, e.Duration); return r; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData e, DbDataReader r, CancellationToken ct = default)
    { if (e.Duration > seuil) alerte(c.CommandText, e.Duration); return new(r); }
}
// dans NewDb : .AddInterceptors(new DetecteurLenteur(TimeSpan.FromMilliseconds(200), (sql, d) => Console.WriteLine($"LENT {d.TotalMilliseconds:N0} ms : {sql[..Math.Min(80, sql.Length)]}")))
```
**À noter** : `e.Duration` mesure l'**exécution jusqu'au retour du lecteur** (pas la lecture de toutes les lignes) ; pour la durée totale, utilisez `DataReaderDisposing` (où `ReadCount` donne les lignes lues) ; l'intercepteur doit rester **rapide** et **sans état partagé non protégé**.

### Sans index / avec index et procédure stockée

Il permet de repérer **quelles requêtes** (et lesquelles par `TagWith`) exigent un index ; il voit aussi les `EXEC proc` lancés via EF (pas ceux de Dapper/ADO.NET).

### 🧪 TP 11.3 — « Budget de requêtes par traitement »

*Énoncé.* Écrivez un intercepteur qui échoue (exception) quand un traitement dépasse 5 requêtes. Appliquez-le à un code contenant un N+1.

<details><summary>Correction</summary>

Utilisez un compteur par **traitement** (`AsyncLocal<int>` ou un service par portée) incrémenté dans `ReaderExecutingAsync` ; levez `InvalidOperationException` au-delà du budget. Le test est un **filet** : il attrape les régressions que la revue de code laisse passer.
</details>

---

## 11.4 Compteurs et métriques EF Core

**Axe : outillage — ⚙️ .NET (continu).**

### Ce que c'est

EF Core publie des **métriques** :
- **EF Core 9 et suivants** : via l'API standard `System.Diagnostics.Metrics`, **meter `Microsoft.EntityFrameworkCore`** (recoupé).
- **Compteurs d'événements (hérités)** : lisibles avec `dotnet-counters`, sans rien modifier dans le programme :

```bash
dotnet-counters ps
dotnet counters monitor --counters Microsoft.EntityFrameworkCore -p <PID>
```
On y lit (recoupé) : **DbContexts actifs**, **requêtes par seconde / totales**, **taux de succès du cache de requêtes**, **`SaveChanges`**, **échecs de la stratégie d'exécution**, **échecs de concurrence optimiste**.

### Dans quels cas

Voir **en direct** le comportement d'une application : le **taux de succès du cache de requêtes** (une valeur qui chute signale des requêtes à constantes variables — 7.1.1), les **contextes actifs** qui montent (fuite : contextes non libérés), le **nombre de requêtes** par seconde.

### Sans l'outil

On regarde le temps de réponse global, sans savoir si c'est le nombre de requêtes, le cache ou le pool qui pose problème.

### Avec l'outil

```bash
# Dans un terminal : lancer le programme de test (par exemple la boucle de 2.5, constantes vs paramètres)
# Dans un autre :
dotnet counters monitor --counters Microsoft.EntityFrameworkCore -p <PID>
```
**À observer** : pendant la boucle `EF.Constant` : le **taux du cache de requêtes** s'effondre ; pendant la boucle avec paramètre : il tient à ~100 % ; « Queries (Count / 1 sec) » donne le débit.

**En code** (lecture des métriques sans outil externe) :

```csharp
using var ecouteur = new System.Diagnostics.Metrics.MeterListener();
ecouteur.InstrumentPublished = (instr, l) => { if (instr.Meter.Name == "Microsoft.EntityFrameworkCore") l.EnableMeasurementEvents(instr); };
ecouteur.SetMeasurementEventCallback<long>((instr, valeur, tags, _) => Console.WriteLine($"{instr.Name} = {valeur}"));
ecouteur.Start();
```
*(Les noms et types d'instruments exacts : à lister dans votre version avec `InstrumentPublished`.)*

### Sans index / avec index et procédure stockée

Sans objet : ces compteurs décrivent le **côté EF** (les procédures lancées par EF y sont comptées comme des requêtes).

### 🧪 TP 11.4 — « Le cache de requêtes sous la loupe »

*Énoncé.* Reproduisez 2 000 requêtes à constantes (`EF.Constant`) puis 2 000 à paramètre. Notez le **taux de succès du cache** et le débit dans `dotnet-counters`. Que concluez-vous sur le coût de la première forme ?

<details><summary>Correction</summary>

Avec des constantes, le taux de succès tombe (chaque valeur est une forme nouvelle) et le débit baisse ; avec un paramètre, ~100 % et un débit plus élevé. C'est la **vue en direct** du phénomène mesuré en 0.4 et 7.1.1.
</details>

---

## 11.5 OpenTelemetry et traces

**Axe : outillage — exploitation.**

### Ce que c'est

OpenTelemetry permet de **collecter et exporter** métriques et traces vers un outil (Prometheus, Application Insights, Jaeger…). Pour **EF Core**, le meter `Microsoft.EntityFrameworkCore` (11.4) est consommable par OpenTelemetry :

```csharp
// builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Microsoft.EntityFrameworkCore").AddConsoleExporter());
```
*(Nécessite les paquets OpenTelemetry ; non installés ici.)* Pour les **traces de commandes SQL** (durée de chaque requête dans une trace distribuée), l'instrumentation passe par le **fournisseur de données** (`Microsoft.Data.SqlClient`) et le paquet d'instrumentation SQL d'OpenTelemetry *(non recoupé : vérifiez le paquet et ses options)*.

### Dans quels cas

Applications en production avec un outil d'observabilité ; corrélation « requête HTTP → requêtes SQL ».

### Sans l'outil

On corrèle à la main les journaux d'application et ceux de la base.

### Avec l'outil

Une trace montre, pour une requête HTTP, la liste des commandes SQL, leur durée et leur ordre : un N+1 y est **visible** (200 spans identiques). En local, un `ActivityListener` en mémoire suffit pour vérifier la présence de spans :

```csharp
using var l = new System.Diagnostics.ActivityListener
{ ShouldListenTo = s => true, Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
  ActivityStarted = a => Console.WriteLine($"{a.Source.Name} : {a.DisplayName}") };
System.Diagnostics.ActivitySource.AddActivityListener(l);
```
**À observer** : quelles sources émettent des activités lors d'un appel EF (selon la version et le fournisseur).

### Sans index / avec index et procédure stockée

Une trace donne la **durée côté application** d'une requête ; combinez avec le plan (11.8) pour savoir si un index aide.

### 🧪 TP 11.5 — « Quelles sources émettent ? »

*Énoncé.* Branchez l'écouteur ci-dessus, exécutez une requête EF et listez les sources d'activités et de métriques observées. Lesquelles retenez-vous pour la production ?

<details><summary>Correction</summary>

Le résultat dépend des versions d'EF Core et de SqlClient ; la **réponse fiable** est celle de votre propre exécution (liste réelle des sources). Documentez-la : c'est la base de la configuration OpenTelemetry.
</details>

---

## 11.6 BenchmarkDotNet

**Axe : outillage — ⚙️ .NET (CPU, allocations).**

### Ce que c'est

BenchmarkDotNet exécute un code **plusieurs fois**, gère l'**échauffement**, le **JIT**, les **statistiques**, et rapporte temps moyen, écart-type, **allocations** (`[MemoryDiagnoser]`) et GC. **Il n'est pas installé** dans ce workshop : mon `Mesurer` est un substitut simple.

### Dans quels cas

Comparer des **micro-optimisations** (requête compilée vs normale, `CountBy` vs `GroupBy`, Dapper vs ADO.NET sur une requête triviale). **Pas adapté** à une requête qui dépend fortement du cache SQL et du réseau (bruit énorme) : pour cela, les **logical reads** (11.7) sont plus fiables.

### Sans l'outil

```csharp
var sw = Stopwatch.StartNew(); /* une seule exécution */ Console.WriteLine(sw.ElapsedMilliseconds);     // JIT, cache froid, bruit
```

### Avec l'outil

Dans un **projet séparé** (jamais dans la solution qui contient du code applicatif de production) :

```bash
dotnet new console -n Bench && cd Bench
dotnet add package BenchmarkDotNet
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
```
```csharp
using BenchmarkDotNet.Attributes; using BenchmarkDotNet.Running;
[MemoryDiagnoser]
public class BenchLectureParCle
{
    private StarWarsContext _db = default!;
    [GlobalSetup] public void Init() => _db = /* NewDb() */ null!;
    [Benchmark(Baseline = true)] public Task<Good?> Normale()  => _db.Goods.AsNoTracking().FirstOrDefaultAsync(g => g.Id == 42);
    [Benchmark]                  public Task<Good?> Compilee() => Compilees.BienParId(_db, 42);
}
public class Program { public static void Main() => BenchmarkRunner.Run<BenchLectureParCle>(); }
```
```bash
dotnet run -c Release
```
**À observer** : colonnes `Mean`, `Error`, `StdDev`, `Ratio`, `Allocated`. **Pièges** : `Release` obligatoire, pas de débogueur, **un contexte par benchmark** ne se partage pas entre threads, ne pas mesurer la création de la chaîne de connexion.

### Sans index / avec index et procédure stockée

Utile pour **isoler le coût .NET** à SQL constant (donc comparer LINQ, compilé, Dapper, procédure sur la même requête triviale) ; inutile pour mesurer l'effet d'un index (ce sont les lectures SQL qui le disent).

### 🧪 TP 11.6 — « Le bruit de la mesure »

*Énoncé.* Mesurez la même requête 10 fois avec `Stopwatch` simple, puis avec `Mesurer` (échauffement + médiane), puis avec BenchmarkDotNet. Comparez l'écart-type de chaque méthode.

<details><summary>Correction</summary>

L'écart-type diminue avec la rigueur de la méthode ; BenchmarkDotNet donne aussi l'intervalle de confiance. L'enseignement : sans échauffement ni répétition, **les comparaisons de moins de ~20 %** sont du bruit.
</details>

---

## 11.7 `SET STATISTICS IO / TIME` et statistiques de session

**Axe : outillage — 🗄️ SQL.**

### Ce que c'est

`SET STATISTICS IO, TIME ON` produit, **pour chaque instruction**, les **lectures logiques et physiques**, le **nombre de parcours** (`Scan count`), le **temps CPU** et la **durée**. Les **lectures logiques** sont l'indicateur **stable** pour comparer deux versions d'une requête.

### Dans quels cas

Chaque optimisation SQL : index, sargabilité, projection, jointures (chapitres 1 à 8).

### Sans l'outil

On compare des durées à l'oreille, avec un cache tantôt froid tantôt chaud.

### Avec l'outil

```sql
SET STATISTICS IO, TIME ON;
SELECT COUNT(*) FROM dbo.Trades WHERE Quantity = 500;
-- Table 'Trades'. Scan count 1, logical reads …, physical reads …, read-ahead reads …
-- SQL Server Execution Times: CPU time = … ms, elapsed time = … ms
```
**Lire** : `logical reads` (pages en mémoire), `physical reads` + `read-ahead reads` (disque : cache froid), `Scan count`, `lob logical reads` (colonnes `max`) ; CPU (temps de calcul, plus élevé que la durée en parallèle).

**Depuis .NET, de façon programmée.** La réception des messages `InfoMessage` de SqlClient peut être **incomplète** avec EF (les lignes d'I/O ne remontent pas toujours) : la méthode **fiable** est de lire les compteurs de **session** à la fermeture de chaque connexion (le pool les **réinitialise** à la réutilisation, donc on relève à chaque restitution) :

```csharp
public sealed class StatsSession : DbConnectionInterceptor
{
    public static long LogicalReads, CpuMs, ElapsedMs;
    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection c, ConnectionEventData e, InterceptionResult r)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CAST(logical_reads AS bigint), CAST(cpu_time AS bigint), CAST(total_elapsed_time AS bigint) FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        await using var rd = await cmd.ExecuteReaderAsync();
        if (await rd.ReadAsync()) { Interlocked.Add(ref LogicalReads, rd.GetInt64(0)); Interlocked.Add(ref CpuMs, rd.GetInt64(1)); Interlocked.Add(ref ElapsedMs, rd.GetInt64(2)); }
        return r;
    }
}
```
**À observer** : les totaux avant/après un bloc de requêtes. **Limites** : à utiliser en test, pas en production (une requête par fermeture) ; les compteurs de session ne comptent que les requêtes **terminées**.

### Sans index / avec index

C'est **l'instrument de référence** de la comparaison avec/sans index : relevez `logical reads` avant et après `CREATE INDEX`.

### Comparaison avec une procédure stockée

Les statistiques de procédure sont dans `sys.dm_exec_procedure_stats` (11.9) ; en session, `STATISTICS IO` affiche aussi les lectures des instructions **à l'intérieur** de la procédure.

### 🧪 TP 11.7 — « Cache froid, cache chaud »

*Énoncé.* Exécutez une requête en cache froid (`DBCC DROPCLEANBUFFERS`) puis chaud. Remplissez : logical reads, physical reads, CPU, durée. Quel nombre est identique et pourquoi est-il l'indicateur à privilégier ?

<details><summary>Correction</summary>

Les **logical reads** sont identiques (même plan, mêmes pages) ; les physical reads et la durée varient. D'où leur valeur pour comparer des versions d'une requête indépendamment de l'état du cache.
</details>

---

## 11.8 Plan d'exécution réel

**Axe : outillage — 🗄️ SQL (compréhension).**

### Ce que c'est

Le **plan réel** montre l'arbre d'opérateurs **effectivement exécuté** avec les **lignes estimées et réelles**, les **avertissements** (spills, conversions implicites, index manquants), la **mémoire accordée**, le **parallélisme**. Le plan **estimé** (sans exécution) montre ce que l'optimiseur **pense** devoir faire.

### Dans quels cas

Dès qu'une requête est lente sans raison évidente ; pour **comprendre** l'effet d'un index.

### Sans l'outil

On crée des index « au hasard » en espérant que l'un serve.

### Avec l'outil

Plan **réel** : outil graphique (**Actual Execution Plan**) ou `SET STATISTICS XML ON`. Plan **estimé** : `SET SHOWPLAN_XML ON` (la requête n'est pas exécutée). Plan **en cache** :

```sql
SELECT TOP 5 st.text, qp.query_plan
FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
WHERE st.text LIKE '%lab:S3%';
```
**Lecture rapide** (voir la grille du 00) : *Seek* vs *Scan* ; *Key Lookup* ; flèches **épaisses** (nombre de lignes) ; **écart estimé/réel** ; **icône d'avertissement** ; `Sort`/`Hash Match` (mémoire) ; le **coût relatif** en pourcentage (un indicateur, pas une mesure).

**Plan réel d'une requête en production** *(non recoupé : dépend de la version et d'une option de base)* : les plans réels dernière exécution sont disponibles via `sys.dm_exec_query_plan_stats` si `LAST_QUERY_PLAN_STATS` est activé dans la configuration de la base.

### Sans index / avec index

Le plan est **la preuve** qu'un index est utilisé ou non (*Seek* vs *Scan*) ; comparez avant/après pour chaque index créé dans les chapitres précédents.

### Comparaison avec une procédure stockée

Le plan d'une procédure est mis en cache **par procédure** et compilé pour le **premier jeu de paramètres** (sniffing, 2.5, 7.5.2) : comparez le plan pour deux valeurs.

### 🧪 TP 11.8 — « Annoter trois plans »

*Énoncé.* Pour trois requêtes des chapitres précédents (non sargable, sargable avec index, sniffing), imprimez les plans réels et annotez : opérateur d'accès, lignes estimées/réelles, avertissements. Rédigez en trois phrases ce que le plan dit que le SQL seul ne dit pas.

<details><summary>Correction</summary>

Le SQL dit **quoi** ; le plan dit **comment** et **combien** : l'algorithme de jointure, l'ordre d'accès, la **sélectivité estimée**, la mémoire. Un mauvais plan s'explique presque toujours par une **estimation** fausse (statistiques, sniffing) ou un **index** inadapté.
</details>

---

## 11.9 Query Store, DMV et Extended Events

**Axe : outillage — 🗄️ SQL (production, historique).**

### Ce que c'est

| Outil | Rôle |
|---|---|
| **DMV** (`sys.dm_exec_query_stats`, `sys.dm_exec_procedure_stats`, `sys.dm_exec_requests`) | **état courant** et statistiques **cumulées depuis le dernier vidage du cache** |
| **Query Store** | **historique persistant** des requêtes, plans et statistiques, par intervalle ; détection des **régressions de plan**, **forçage** d'un plan |
| **Extended Events** | **capture ciblée** d'événements (requêtes dépassant un seuil) à faible coût |

### Dans quels cas

Production : « qu'est-ce qui consomme le plus ? », « depuis quand cette requête est-elle lente ? », « le plan a-t-il changé ? ».

### Sans l'outil

Les DMV sont **vidées** au redémarrage ou au vidage du cache : on perd l'histoire.

### Avec l'outil

**DMV — les plus coûteuses, par tag EF (voir 6.11)** :

```sql
SELECT TOP 10 qs.execution_count, qs.total_logical_reads / qs.execution_count AS reads_moy, qs.total_worker_time / qs.execution_count / 1000.0 AS cpu_ms_moy, SUBSTRING(st.text, 1, 120) AS debut
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
ORDER BY qs.total_worker_time DESC;
```
**Query Store** (option de base à **désactiver après essai**) :

```sql
ALTER DATABASE StarWarsTrade SET QUERY_STORE = ON;
-- exécuter vos requêtes, puis :
SELECT TOP 10 q.query_id, qt.query_sql_text, rs.avg_duration / 1000.0 AS duree_ms, rs.avg_logical_io_reads, rs.count_executions
FROM sys.query_store_query q JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
JOIN sys.query_store_plan p ON p.query_id = q.query_id JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
ORDER BY rs.avg_duration DESC;
```
Puis, dans l'outil graphique, « **Regressed Queries** » et « **Top Resource Consuming Queries** » ; pour **forcer** un plan : `EXEC sp_query_store_force_plan @query_id, @plan_id;`.

**Extended Events — capturer les requêtes de plus de 500 ms** *(noms d'événements et de colonnes à vérifier pour votre version)* :

```sql
CREATE EVENT SESSION lab_lent ON SERVER
ADD EVENT sqlserver.rpc_completed (ACTION (sqlserver.sql_text) WHERE duration > 500000)       -- microsecondes
ADD TARGET package0.ring_buffer WITH (MAX_MEMORY = 4096 KB);
ALTER EVENT SESSION lab_lent ON SERVER STATE = START;
-- … exécuter les requêtes lentes, puis lire :
SELECT CAST(target_data AS xml) FROM sys.dm_xe_session_targets st JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address WHERE s.name = 'lab_lent';
```
**À observer** : les requêtes EF apparaissent comme `sp_executesql` ; sans `TagWith`, impossible de les relier au code.

### Sans index / avec index

Les **index manquants suggérés** (6.7) se lisent dans les DMV ; le Query Store montre l'**effet d'un index** sur la durée moyenne **avant/après** sa création.

### Comparaison avec une procédure stockée

`sys.dm_exec_procedure_stats` donne **par procédure** exécutions, CPU, lectures : plus lisible qu'une requête ad hoc — un avantage réel des procédures pour l'**exploitation**.

### 🧪 TP 11.9 — « Retrouver une régression »

*Énoncé.* Activez le Query Store, exécutez la requête du sniffing (2.5) avec deux ordres de seuils, puis retrouvez **les deux plans** du même `query_id`. Forcez le meilleur et vérifiez l'effet. Désactivez le Query Store.

<details><summary>Correction</summary>

`sys.query_store_plan` montre plusieurs `plan_id` pour un `query_id` ; comparez leurs `avg_logical_io_reads` ; `sp_query_store_force_plan` fige le plan retenu (et `sp_query_store_unforce_plan` l'annule). Forcer un plan est un **pansement** : le sniffing reste à traiter (index, constante, `RECOMPILE`).
</details>

---

## 11.10 Tests d'intégration et garde-fous

**Axe : outillage — qualité continue.**

### Ce que c'est

Les chapitres précédents donnent des **mesures ponctuelles** ; en continu, on **empêche** la régression : tests qui vérifient **le nombre de requêtes**, **l'absence de requêtes lentes**, **la forme du SQL**, sur une **vraie base SQL Server** (pas le fournisseur en mémoire, dont la sémantique diverge : voir 0.7).

### Dans quels cas

Équipes de plusieurs personnes ; chemins critiques ; passage d'une version d'EF à une autre.

### Sans l'outil

Une régression N+1 n'apparaît qu'en production, sous charge.

### Avec l'outil

```csharp
// Budget de requêtes (CompteurSql du 00)
static async Task<T> Budget<T>(CompteurSql c, int max, Func<Task<T>> action)
{ c.Reset(); var r = await action(); if (c.Requetes > max) throw new Exception($"{c.Requetes} requêtes (max {max})"); return r; }

// Garde de forme SQL
static void ContientWhere(IQueryable q) { if (!q.ToQueryString().Contains("WHERE")) throw new Exception("pas de filtre SQL"); }

// Garde de lenteur : le DetecteurLenteur de 11.3 qui lève au lieu d'alerter
```
**Base de test** : un conteneur SQL Server éphémère par exécution de tests (ex. avec **Testcontainers** *(non installé ; non recoupé)*) et un **jeu de données réduit** mais **représentatif** (distributions asymétriques). Un test de performance ne **mesure pas des millisecondes** ; il vérifie des **invariants** : nombre de requêtes, forme, lectures logiques sous un seuil.

### Sans index / avec index

Un test peut vérifier **qu'un index existe** (`SELECT 1 FROM sys.indexes WHERE name = …`) et que le plan l'utilise (`Index Seek` dans le plan en XML) — utile quand une migration peut le supprimer par erreur.

### Comparaison avec une procédure stockée

Les procédures se testent comme du code (tests tSQLt ou appels depuis les tests d'intégration) ; leur plan et leurs lectures se vérifient de la même façon.

### 🧪 TP 11.10 — « Un filet de sécurité »

*Énoncé.* Écrivez trois tests pour une méthode de service : budget de requêtes, forme SQL (présence de `WHERE`, absence de `SELECT *` équivalent), lectures logiques sous un seuil. Cassez volontairement la méthode (retirez un `Include`/ajoutez un N+1) et vérifiez que chaque test échoue.

<details><summary>Correction</summary>

Le test de budget attrape le N+1 ; le test de forme attrape la perte du `WHERE` (ou du `Select`) ; le test de lectures attrape la perte d'un index. Chacun couvre **une** classe de régression : c'est leur **combinaison** qui protège.
</details>

---

## Script de nettoyage

```sql
-- Extended Events
IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'lab_lent') DROP EVENT SESSION lab_lent ON SERVER;

-- Query Store (option de base) : à désactiver si vous l'avez activé
ALTER DATABASE StarWarsTrade SET QUERY_STORE = OFF;

-- Vérification : ne doit rien retourner
SELECT name FROM sys.server_event_sessions WHERE name LIKE 'lab[_]%';
SELECT actual_state_desc FROM sys.database_query_store_options;      -- OFF
```
