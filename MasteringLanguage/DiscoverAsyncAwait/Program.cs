//// Program.cs — Exemple 1 : les états d'une Task
//using System.Diagnostics.CodeAnalysis;

//Console.WriteLine("| Tâche        | IsCompleted | Successfully | IsFaulted | IsCanceled | Status");
//Console.WriteLine("|--------------|-------------|--------------|-----------|------------|--------");

//// 1. Une tâche déjà terminée avec un résultat
//Task<int> terminee = Task.FromResult(42);

//Console.WriteLine("Task 01");
////Afficher("terminée", terminee);

////// 2. Une tâche en cours
//Task<int> enCours = LongueAsync();
////Console.WriteLine("Task 02");
////Afficher("en cours", enCours);

////// 3. Une tâche en erreur
////Task<int> enErreur = Task.FromException<int>(new InvalidOperationException("boum"));
////Afficher("en erreur", enErreur);

////// 4. Une tâche annulée
////var cts = new CancellationTokenSource();
////cts.Cancel();
////Task<int> annulee = Task.FromCanceled<int>(cts.Token);
////Afficher("annulée", annulee);

////Console.WriteLine();
////Console.WriteLine("On attend la tâche en cours...");
////await enCours;
////Afficher("en cours", enCours);

////// On observe l'exception pour éviter une TaskUnobservedException au GC (voir 3.5)
////_ = enErreur.Exception;

//static async Task<int> LongueAsync()
//{
//    await Task.Delay(200);
//    return 7;
//}

////static void Afficher(string nom, Task tache) =>
////    Console.WriteLine($"| {nom,-12} | {tache.IsCompleted,-11} | {tache.IsCompletedSuccessfully,-12} " +
////                      $"| {tache.IsFaulted,-9} | {tache.IsCanceled,-10} | {tache.Status}");

/////* Sortie attendue :

////| Tâche        | IsCompleted | Successfully | IsFaulted | IsCanceled | Status
////|--------------|-------------|--------------|-----------|------------|--------
////| terminée     | True        | True         | False     | False      | RanToCompletion
////| en cours     | False       | False        | False     | False      | WaitingForActivation
////| en erreur    | True        | False        | True      | False      | Faulted
////| annulée      | True        | False        | False     | True       | Canceled

////On attend la tâche en cours...
////| en cours     | True        | True         | False     | False      | RanToCompletion
////*/
///
// Program.cs — Exemple 2 : quand la méthode async s'exécute-t-elle ?
//Console.WriteLine("1 — avant l'appel");

//Task tache = TravaillerAsync();          // <- l'appel, PAS l'await

//Console.WriteLine("3 — après l'appel, avant l'await");

//await tache;

//Console.WriteLine("5 — après l'await");

//static async Task TravaillerAsync()
//{
//    Console.WriteLine("2 —   début de TravaillerAsync");
//    await Task.Delay(100);               // <- premier await NON terminé : la méthode rend la main
//    Console.WriteLine("4 —   fin de TravaillerAsync");
//}

// Program.cs — Exemple 3 : await sur une tâche déjà terminée
//Console.WriteLine("=== Cas A : await sur une tâche NON terminée ===");
//await Scenario(dejaTerminee: false);

//Console.WriteLine();
//Console.WriteLine("=== Cas B : await sur une tâche DÉJÀ terminée ===");
//await Scenario(dejaTerminee: true);

//static async Task Scenario(bool dejaTerminee)
//{
//    Console.WriteLine("1 — avant l'appel");

//    Task tache = TravaillerAsync(dejaTerminee);

//    Console.WriteLine("3 — après l'appel, avant l'await");

//    await tache;

//    Console.WriteLine("5 — après l'await");
//}

//static async Task TravaillerAsync(bool dejaTerminee)
//{
//    Console.WriteLine("2 —   début de TravaillerAsync");

//    if (dejaTerminee)
//        await Task.CompletedTask;        // tâche DÉJÀ terminée : aucune suspension
//    else
//        await Task.Delay(100);           // tâche non terminée : la méthode rend la main

//    Console.WriteLine("4 —   fin de TravaillerAsync");
//}

//Console.WriteLine("=== Version avec await ===");
//await AvecAwait();

//Console.WriteLine();
//Console.WriteLine("=== Version écrite à la main ===");
//SansAwait(() => Console.WriteLine("terminé (version manuelle)"));

//await Task.Delay(300);                   // laisser la continuation s'exécuter

//static async Task AvecAwait()
//{
//    var valeur = await ProduireAsync();
//    Console.WriteLine($"reçu : {valeur}");
//}

//static void SansAwait(Action quandFini)
//{
//    // 1. On obtient l'awaiter
//    var awaiter = ProduireAsync().GetAwaiter();

//    // 2. On teste si c'est DÉJÀ fini
//    if (awaiter.IsCompleted)
//    {
//        Console.WriteLine($"reçu immédiatement : {awaiter.GetResult()}");
//        quandFini();
//        return;
//    }

//    // 3. Sinon : on enregistre la suite, et on rend la main TOUT DE SUITE
//    Console.WriteLine("pas encore prêt, on enregistre la continuation et on rend la main");

//    awaiter.OnCompleted(() =>
//    {
//        Console.WriteLine($"reçu plus tard : {awaiter.GetResult()}");
//        quandFini();
//    });
//}

//static async Task<int> ProduireAsync()
//{
//    await Task.Delay(100);
//    return 42;
//}

//using System.Diagnostics;

//Console.WriteLine($"Thread au démarrage       : {Environment.CurrentManagedThreadId}");
//Console.WriteLine($"Threads du pool utilisés  : {ThreadPool.ThreadCount}");
//Console.WriteLine();

//Console.WriteLine("--- 100 attentes asynchrones simultanées ---");

//var chrono = Stopwatch.StartNew();

//var taches = Enumerable.Range(0, 100)
//    .Select(_ => AttendreAsync())
//    .ToArray();

//await Task.WhenAll(taches);
//chrono.Stop();

//Console.WriteLine($"Durée                     : {chrono.ElapsedMilliseconds} ms");
//Console.WriteLine($"Threads du pool utilisés  : {ThreadPool.ThreadCount}");
//Console.WriteLine($"Threads distincts vus     : {taches.Select(t => t.Result).Distinct().Count()}");

//static async Task<int> AttendreAsync()
//{
//    await Task.Delay(200);                       // attente PURE : aucun calcul
//    return Environment.CurrentManagedThreadId;
//}

// On limite volontairement le pool pour rendre l'effet visible en quelques secondes
using System.Diagnostics;

///
ThreadPool.SetMinThreads(4, 4);

Console.WriteLine("--- 50 opérations, version BLOQUANTE (.Wait) ---");
var bloquant = Mesurer(() =>
{
    var taches = Enumerable.Range(0, 50)
        .Select(_ => Task.Run(() => AttendreAsync().Wait()))     // <- bloque un thread du pool
        .ToArray();

    Task.WaitAll(taches);
});

Console.WriteLine($"Durée : {bloquant} ms");
Console.WriteLine($"Threads du pool : {ThreadPool.ThreadCount}");
Console.WriteLine();

Console.WriteLine("--- 50 opérations, version ASYNCHRONE (await) ---");
var chrono = Stopwatch.StartNew();

await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => AttendreAsync()));

chrono.Stop();
Console.WriteLine($"Durée : {chrono.ElapsedMilliseconds} ms");
Console.WriteLine($"Threads du pool : {ThreadPool.ThreadCount}");

static async Task AttendreAsync() => await Task.Delay(200);

static long Mesurer(Action action)
{
    var sw = Stopwatch.StartNew();
    action();
    sw.Stop();
    return sw.ElapsedMilliseconds;
}

