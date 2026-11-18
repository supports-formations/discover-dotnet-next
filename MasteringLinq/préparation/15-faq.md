# 15 — FAQ : 50 questions possibles et leurs réponses

> Accompagne le workshop de 2 h ([14](14-workshop-2h.md)) et celui de 5 h ([13](13-deroule.md)). Chaque réponse est **courte** et renvoie au **module** qui l'approfondit.
> **Fiabilité.** Les réponses reprennent le contenu des modules 00 à 12. Les points qui dépendent d'une **version** sont indiqués ; ceux qui n'ont pas été recoupés avec la documentation officielle sont marqués *(à vérifier)*. Aucun chiffre n'est donné : **mesurez** sur votre base.

## Index

[A. Fondamentaux (1–10)](#a-fondamentaux) · [B. Écrire de bonnes requêtes (11–20)](#b-écrire-de-bonnes-requêtes) · [C. SQL, index et plans (21–30)](#c-sql-index-et-plans) · [D. Millions de lignes (31–38)](#d-millions-de-lignes) · [E. Quand sortir de LINQ (39–45)](#e-quand-sortir-de-linq) · [F. Outils, versions, pratiques (46–50)](#f-outils-versions-pratiques)

---

## A. Fondamentaux

**1. Quelle est la différence entre `IEnumerable<T>` et `IQueryable<T>` ?**
`IQueryable` transporte un **arbre d'expression** que EF traduit en SQL ; `IEnumerable` transporte des **délégués** exécutés en mémoire. Le compilateur choisit selon le **type de la variable** : `Where(Func)` appelle `Enumerable.Where` (toute la table est lue), `Where(Expression)` appelle `Queryable.Where` (filtre en SQL). → module 00, 0.1.

**2. Pourquoi ma requête LINQ « ne fait rien » quand je l'écris ?**
Elle est **différée** : c'est une recette. Le SQL part à l'énumération (`ToList`, `First`, `Count`, `Any`, `foreach`…). Conséquence : énumérer deux fois = **deux requêtes**. → 00, 0.2.

**3. Quand utiliser `AsEnumerable()` ?**
Quand **une partie** du filtre n'est pas traduisible **et** que l'ensemble est **déjà réduit** par un `Where` SQL placé avant. Jamais au début d'une requête sur une grosse table. → 00, 0.5 ; 12, 12.1.

**4. Pourquoi mon `Where` avec un `Func` charge-t-il toute la table ?**
Parce que `Func<T,bool>` ne peut pas être traduit : EF lit les lignes (sans `WHERE`) et .NET filtre. Même effet avec une variable typée `IEnumerable<T>` ou une méthode de dépôt qui prend un `Func`. Correction : `Expression<Func<T,bool>>` / `IQueryable<T>`. → 00, 0.1 ; 12, 12.18.

**5. Quelle différence entre `ToList()` et un `foreach`/`AsAsyncEnumerable` ?**
`ToList` **tamponne** tout le résultat avant de rendre la main ; `foreach` sur `AsAsyncEnumerable` **diffuse** ligne à ligne (mémoire bornée) mais garde la requête ouverte côté SQL. Avec suivi activé, chaque entité est conservée : le flux ne protège pas la mémoire sans `AsNoTracking`. → 00, 0.3.

**6. Quand faut-il `AsNoTracking()` ?**
Pour **toute lecture seule**. Pour les entités que vous modifierez puis enregistrerez, gardez le suivi (sinon `SaveChanges` renvoie 0 sans erreur). Le suivi n'agit pas sur le SQL, seulement sur la mémoire et le CPU .NET. → 04, 4.1, 4.4.

**7. Que coûte exactement le suivi des changements ?**
Un instantané des valeurs, une entrée dans la table d'identité, le *fixup* des navigations, et un `DetectChanges` **proportionnel au nombre d'entités suivies** à chaque `SaveChanges`. → 04, 4.3 ; 08, 8.11.

**8. Pourquoi EF ajoute-t-il `OR … IS NULL` à mon `!=` ?**
Pour reproduire la sémantique C# (`null != 5` est vrai), alors que SQL traite `NULL <> 5` comme inconnu. Cela peut changer le **résultat** (beaucoup plus de lignes) et **gêner un seek**. Options : condition explicite (`!= null && …`) ou `UseRelationalNulls()`. → 00, 0.7 ; 02, 2.7.

**9. `ToQueryString()` ou `LogTo` ?**
`ToQueryString()` donne le SQL **sans l'envoyer** (outil de développement) ; `LogTo`/`ILogger` montrent ce qui **est réellement parti**, avec durée et (en dev) valeurs. Ils sont complémentaires. → 02, 2.1 ; 11, 11.1–11.2.

**10. Combien d'allers-retours fait une requête avec trois `Include` ?**
En *single query* (défaut) : **une seule**, mais avec un risque d'explosion cartésienne. Avec `AsSplitQuery()` : **1 + le nombre de collections** (ici 4). → 03, 3.2, 3.7.

---

## B. Écrire de bonnes requêtes

**11. `Any()` ou `Count() > 0` ?**
`Any()` : il se traduit en `EXISTS` et s'arrête à la première ligne ; `Count` parcourt tout ce qui correspond. Le seul cas où ils se valent : **aucune ligne** sans index (les deux scannent). Depuis EF 9, `collection.Count != 0` dans un `Where` devient `EXISTS`, mais pas le `CountAsync() > 0` terminal. → 01, 1.3 ; 09, 9.6.

**12. Quelle différence entre `First`, `FirstOrDefault`, `Single`, `Last` ?**
`First` → `TOP(1)` (exception si vide) ; `FirstOrDefault` → `null`/défaut ; `Single` → `TOP(2)` pour détecter les doublons (exception si 0 ou ≥ 2) ; `Last` **exige** un `OrderBy`. Sans tri, le « premier » dépend du plan et peut **changer** après un index. → 01, 1.3 ; 12, 12.10.

**13. Pourquoi `Skip/Take` sans `OrderBy` est-il dangereux ?**
SQL Server ne garantit **aucun ordre** sans `ORDER BY` : les pages peuvent se recouvrir ou omettre des lignes. Même avec un tri non unique, les ex æquo sont ordonnés au hasard du plan : ajoutez une **clé unique** en dernier critère. → 01, 1.2 ; 12, 12.9.

**14. Pourquoi la page 100 000 est-elle plus lente que la page 1, et que faire ?**
`OFFSET` lit puis jette les lignes précédentes : le coût **croît avec la profondeur**. La **pagination par clé** (`Where(clé > dernière)`) a un coût constant **si** un index suit l'ordre de tri. Elle n'autorise pas le saut direct à une page arbitraire. → 07, 7.3.1.

**15. `Include` ou `Select` ?**
`Include` charge **toutes les colonnes** et **suit** les entités : à utiliser quand on modifiera ces entités. Pour afficher quelques champs : projection (`Select`), moins de colonnes, pas de suivi, index couvrant possible. → 03, 3.1, 3.3 ; 06, 6.13.

**16. Qu'est-ce que l'explosion cartésienne et le split query la résout-il toujours ?**
Avec plusieurs collections incluses en une requête, chaque combinaison enfant A × enfant B devient une ligne (produit des cardinalités, parent répété). `AsSplitQuery` passe à une requête par collection (somme). **Contreparties** : plusieurs allers-retours, **lecture non atomique** (utiliser une transaction `Snapshot` ou rester en *single*), pas utile avec une seule collection. → 03, 3.2, 3.7, 3.9.

**17. Comment détecter et corriger un N+1 ?**
Détecter : **compter les requêtes** (intercepteur, test avec budget), une même requête avec un `execution_count` énorme dans les DMV, un « peigne » dans les traces. Corriger : `Include`, projection, une requête `Contains` par lots, `GroupBy` côté SQL, chargement groupé avec *fixup*. → 03, 3.10 ; 11, 11.10.

**18. `Union` ou `Concat` ?**
`Union` dédoublonne (tri/hachage de tout) ; `Concat` (`UNION ALL`) n'ajoute que la concaténation. Utilisez `Concat` quand les ensembles sont **disjoints** ou que les doublons sont acceptés ; `Union` seulement si le dédoublonnage est voulu. Si les deux conditions portent sur la même table, un `OR` est souvent préférable. → 01, 1.7 ; 06, 6.9.

**19. `GroupBy` est-il traduit en SQL ?**
Oui quand la projection ne contient que la **clé** et des **agrégats** (`Count`, `Sum`, `Average`, `Min`, `Max`) ; un `Where` après `GroupBy` devient `HAVING`. La traduction des **éléments** d'un groupe (`g.OrderBy(...).First()`) dépend de la version : vérifiez avec `ToQueryString()`. → 01, 1.5.

**20. `LeftJoin` ou `GroupJoin` + `DefaultIfEmpty` ?**
Depuis .NET 10 / EF Core 10, `LeftJoin` (et `RightJoin`) existent et sont traduits : plus lisible et moins sujet à l'erreur (oublier `DefaultIfEmpty` transforme en inner join). Le SQL est en pratique équivalent ; aucun gain de vitesse attendu. → 09, 9.7 ; 01, 1.6.

---

## C. SQL, index et plans

**21. Qu'est-ce qu'un prédicat sargable ?**
Un prédicat que SQL Server peut évaluer **en cherchant dans un index** : colonne seule d'un côté, valeur de l'autre (`col >= @a AND col < @b`, `col = @x`, `LIKE 'abc%'`). Il ne l'est plus avec une **fonction ou un calcul sur la colonne** (`YEAR(col) = 2025`, `LOWER(col) = …`, `col * 2 > 9`, `LIKE '%x'`). → 02, 2.9 ; 06, 6.8.

**22. Pourquoi `ToLower()` est-il à éviter ?**
Il applique une fonction à **chaque ligne** : l'index est inutilisable. Dans une base en collation insensible à la casse (ici `…_CI_AS`), l'égalité simple est déjà insensible : `Sku == "x"` suffit et reste sargable. Pour une comparaison sensible à la casse : `EF.Functions.Collate(...)` (au prix d'un scan). → 00, 0.7 ; 12, 12.11.

**23. Un index accélère-t-il toujours ?**
Non. Il accélère **la lecture** des requêtes qui le peuvent utiliser (prédicat sargable, ordre, couverture) et **ralentit l'écriture** (chaque index modifié doit être mis à jour) et consomme de l'espace. Mesurez **les deux**. → 05, 5.1, 5.5 ; 06, 6.7.

**24. Qu'est-ce qu'un index couvrant (`INCLUDE`) et quand l'utiliser ?**
Un index qui contient **toutes les colonnes** lues par la requête (clé pour chercher/trier, `INCLUDE` pour le reste) : plus de *Key Lookup*. Il suppose une **projection** (une entité complète exigerait toutes les colonnes). → 01, 1.1 ; 06, 6.1.

**25. Quand un index filtré est-il utile ?**
Pour un **sous-ensemble sélectif et récurrent** (ex. biens de contrebande, ~8 %). Il est petit et rapide, mais n'est utilisé que si le prédicat de la requête **implique** son filtre (valeurs connues à la compilation). → 08, 8.5 ; 03, 3.12.

**26. Comment lire un plan d'exécution ?**
Regardez : l'opérateur d'accès (**Seek** bon, **Scan** coûteux sur une grosse table), les **Key Lookup**, l'écart **lignes estimées / réelles**, les avertissements (**spill**, conversion implicite), `Sort`/`Hash Match`, le parallélisme. Le « coût relatif » est un indicateur, pas une mesure. → 00 « Boîte à outils » ; 11, 11.8 ; 08, 8.6.

**27. Faut-il comparer des durées ou des `logical reads` ?**
Les **logical reads** : stables, indépendants du cache et de la charge, ils comparent deux versions d'une requête. La durée varie avec le cache froid/chaud, le réseau, la machine. Précisez toujours l'état du cache. → 00, 0.8 ; 11, 11.7.

**28. Qu'est-ce que le *parameter sniffing* et comment le traiter ?**
Le plan est compilé pour la **première** valeur de paramètre et réutilisé : bon pour elle, parfois catastrophique pour d'autres profils. Remèdes : `OPTION (RECOMPILE)` (par intercepteur et tag), `OPTIMIZE FOR UNKNOWN`, constante, requêtes séparées par profil, index mieux conçu (couvrant) qui supprime l'ambiguïté. → 02, 2.5 ; 07, 7.5.1–7.5.2.

**29. Pourquoi des paramètres plutôt que des constantes ?**
Un paramètre donne **une forme de requête** et **un plan** réutilisés ; une constante crée **une forme et un plan par valeur** (cache EF et cache de plans SQL pollués). Le revers : le sniffing (28). Attention aux constructeurs d'expressions (`Expression.Constant`) et à `EF.Constant`. → 00, 0.4 ; 07, 7.1.1 ; 12, 12.20.

**30. `Contains` avec une grande liste : quelles limites ?**
SQL Server limite à **2 100 paramètres** par commande. Selon la version, EF traduit la liste en constantes (anciennes versions), en **un paramètre JSON** lu par `OPENJSON` (EF 8-9) ou en **paramètres multiples** (EF 10, défaut). Modes : `ParameterTranslationMode` et `EF.Constant/Parameter/MultipleParameters`. Au-delà de quelques milliers : lots (`Chunk`), paramètre table, table temporaire. → 02, 2.6 ; 07, 7.4.6 ; 09, 9.4.

---

## D. Millions de lignes

**31. Comment traiter 10 M de lignes sans `OutOfMemoryException` ?**
Ne jamais tout matérialiser : **flux** (`AsAsyncEnumerable`), **projection étroite**, **`AsNoTracking`**, traitement **par lots** à curseur de clé, et mieux encore **faire le calcul dans SQL** quand c'est possible (agrégats, `ExecuteUpdate`). → 08, 8.1–8.3, 8.11.

**32. `ExecuteUpdate`/`ExecuteDelete` ou `SaveChanges` ?**
`ExecuteUpdate` pour les **modifications en masse par critère** : une instruction, aucune entité. Limites : le suivi n'est **pas synchronisé** (entités périmées), les intercepteurs de `SaveChanges`/l'audit **ne s'exécutent pas**, les jetons de concurrence ne sont pas vérifiés, les cascades côté client n'ont pas lieu. Il n'enlève pas le **coût SQL** (journal, index, verrous). → 05, 5.1, 5.3.

**33. Pourquoi découper une grosse mise à jour en lots ?**
Pour limiter le **journal** de transaction et éviter l'**escalade de verrous** (au-delà d'environ 5 000 verrous de ligne, SQL Server pose un verrou de table qui bloque les autres sessions). Les lots rendent la base utilisable pendant le traitement et permettent la reprise. → 08, 8.2, 8.4, 8.13.

**34. `SqlBulkCopy` ou `AddRange` pour insérer ?**
`AddRange` + `SaveChanges` suit chaque entité et envoie des commandes batchées : adapté jusqu'à quelques milliers de lignes. Pour des dizaines/centaines de milliers : `SqlBulkCopy` (flux bulk, journalisation minimale possible), ou `INSERT … SELECT` si les données sont déjà en base. Les index supplémentaires ralentissent toutes les méthodes. → 05, 5.5.

**35. `COUNT(*)` est lent sur ma grosse table : alternatives ?**
Selon le besoin : un **ordre de grandeur** via les métadonnées (`sys.dm_db_partition_stats`) ou un échantillon (`TABLESAMPLE`) ; un **total exact et instantané** via une **vue indexée** (`COUNT_BIG(*)`) ou un compteur incrémenté (au prix de l'écriture) ; pour la pagination, `Take(n+1)` évite le total. → 07, 7.3.2–7.3.3 ; 08, 8.8.

**36. Pagination sur 6 M de lignes : offset ou keyset ?**
Offset pour de petites profondeurs et un accès aléatoire aux pages ; **keyset** pour les parcours séquentiels et les grandes profondeurs (coût constant), avec un index sur l'ordre de tri et une clé unique en dernier critère. → 07, 7.3.1 ; 08, 8.3.

**37. Quand un index *columnstore* est-il pertinent ?**
Pour des **agrégats analytiques** sur des millions de lignes (sommes, regroupements) : lecture colonnaire compressée en mode batch. Il est **coûteux à construire** et alourdit l'écriture ; il ne sert pas aux lectures ponctuelles. → 08, 8.5.

**38. `READ_COMMITTED_SNAPSHOT` ou `NOLOCK` pour ne pas être bloqué par les écritures ?**
`READ_COMMITTED_SNAPSHOT` (versionnement de lignes) : les lecteurs ne sont **pas bloqués** et lisent la dernière version **validée** (coût : `tempdb`). `NOLOCK` / `READ UNCOMMITTED` : jamais bloqué mais **lectures sales**, lignes lues deux fois ou ratées. Pour un rapport fiable : RCSI ou `SNAPSHOT`. C'est une **option de base** : à décider avec les DBA. → 08, 8.13–8.14.

---

## E. Quand sortir de LINQ

**39. Quand faut-il vraiment quitter LINQ ?**
Quand une **mesure** (SQL → mémoire → CPU) montre qu'aucune technique dans EF ne suffit : fenêtrage/`PIVOT`/CTE récursive/`MERGE`, traitement ensembliste lourd, hints ou tables temporaires indispensables, export/insertion de masse, ou chemin chaud dont le pipeline EF pèse **de façon mesurée**. Avant, essayez : projection, index, split, compilation, `FromSql` partiel. → 10, 10.1, 10.3.

**40. Dapper est-il vraiment plus rapide qu'EF Core ?**
Il a un **coût par ligne plus faible** (pas de suivi ni de pipeline de traduction), mais sur une requête SQL non triviale, le temps est dominé par SQL Server : le plan et les lectures sont **les mêmes**. L'écart se voit sur les très nombreux petits appels (hot path) et les grosses lectures (allocations). Mesurez sur **votre** charge. → 10, 10.1.5, 10.2.3.

**41. Une procédure stockée est-elle plus rapide qu'une requête LINQ ?**
Pas en soi : à même requête, **même plan, mêmes lectures**. LINQ paramétré a aussi un plan en cache. Une procédure est **sujette au *sniffing*** comme LINQ. Ses vrais avantages : **droits d'accès** par objet, traitements **multi-étapes**, exploitation (`sys.dm_exec_procedure_stats`), logique partagée entre applications. Inconvénients : non composable depuis LINQ, SQL non vérifié à la compilation. → 10, 10.2.2 ; 00, 0.4, 0.8.

**42. `FromSql` est-il composable ?**
Oui, si c'est un **`SELECT`** : EF l'enveloppe en sous-requête et ajoute `Where`, `OrderBy`, `Take`, `Include`. Pas pour un `EXEC` de procédure, ni pour une CTE (`WITH`), ni avec un `ORDER BY` terminal sans `TOP`. Toujours en **interpolation** (`FromSql($"…")`) pour paramétrer et éviter l'injection. → 05, 5.7 ; 10, 10.2.1.

**43. linq2db ou EF Core ?**
linq2db traduit aussi du LINQ en SQL mais **sans suivi**, avec un DSL de **fenêtrage**, `MERGE`, hints et `BulkCopy`, sans graphe d'entités ni migrations. Il se justifie quand vous voulez **rester en LINQ** et que ces besoins se multiplient ; pour un cas isolé, `FromSql` suffit. → 10, 10.2.4.

**44. CQRS (écriture EF, lecture SQL) : vaut-il le coup ?**
Quand les **lectures lourdes** pénalisent le modèle d'écriture normalisé : on lit dans un **modèle de lecture** (vue, table dénormalisée) avec Dapper/SQL et on écrit avec EF. Coûts : duplication, **cohérence éventuelle**, maintenance du modèle de lecture. À réserver aux cas où la mesure l'impose. → 10, 10.2.8.

**45. Faut-il essayer RepoDB, PetaPoco, OrmLite… ?**
Seulement si **votre mesure** montre un gain sur votre charge par rapport à Dapper/ADO.NET, et si l'écosystème (maintenance, compatibilité) le justifie. Ils n'ont pas été testés dans ce workshop : la comparaison est conceptuelle. → 10, 10.2.7.

---

## F. Outils, versions, pratiques

**46. Quand compiler une requête (`EF.CompileAsyncQuery`) ?**
Sur un **hot path** : une requête **paramétrée, de forme fixe, appelée des milliers de fois par seconde**. Le gain est de l'ordre de quelques dizaines de microsecondes et quelques Ko par appel ; il est invisible dès que le SQL domine. Une requête compilée ne se compose plus. → 00, 0.4 ; 07, 7.1.2.

**47. Quels sont les pièges du pool de `DbContext` ?**
EF réinitialise son état interne mais **pas vos champs/propriétés** : un contexte du pool peut conserver un identifiant de locataire ou un indicateur pour l'emprunteur suivant. Ne stockez pas d'état applicatif dans un contexte poolé. → 07, 7.1.5.

**48. Quelles nouveautés d'EF Core comptent pour la performance ?**
(recoupé avec la documentation officielle) **EF 8** : `Contains` via `OPENJSON`, collections primitives en JSON, `SqlQuery` sur types non mappés. **EF 9** : `Count != 0` → `EXISTS`, `ExecuteUpdate`/`GroupBy` sur types complexes, NativeAOT et requêtes précompilées **expérimentaux**. **EF 10** : `LeftJoin`/`RightJoin`, filtres de requête nommés, `ExecuteUpdate` à lambda ordinaire, paramètres multiples par défaut pour `Contains`, JSON via `ComplexProperty`, vecteurs (SQL Server 2025). **EF 11** : *non recoupé* — lire la page « What's new ». → 09.

**49. Comment tester les performances en intégration continue ?**
Ne pas mesurer des millisecondes (bruit) : tester des **invariants** — **nombre de requêtes** par opération, **forme du SQL** (`WHERE`, colonnes), **lectures logiques** sous un seuil, **présence d'un index** — sur une vraie base SQL Server (pas le fournisseur en mémoire, dont la sémantique diverge). → 11, 11.10.

**50. Que vérifier en revue de code pour éviter les régressions de performance ?**
La check-list du module 12 : pas de `ToList`/`AsEnumerable` avant filtre/tri/compte ; pas de `Func`/`IEnumerable` dans les signatures d'accès aux données ; `OrderBy` déterministe avant `Take/Skip` ; `Any` pour l'existence ; pas de requête dans une boucle ; split ou projection pour plusieurs collections ; agrégats testés sur ensemble vide ; valeurs capturées (pas de constantes variables) ; `AsNoTracking` pour les lectures ; pas de `.Result`/`.Wait()` ; un `DbContext` par tâche concurrente ; filtres globaux inventoriés. → 12.
