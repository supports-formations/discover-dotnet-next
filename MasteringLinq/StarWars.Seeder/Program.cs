using System.Diagnostics;
using Microsoft.Data.SqlClient;
using StarWars.Seeder;
using StarWars.Seeder.Generation;

Options options;
try { options = Options.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Options.HelpText);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(Options.HelpText);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    var total = Stopwatch.StartNew();
    var tables = TableSpecs.Build(options.Scale);

    Console.WriteLine($"🌌 StarWars.Seeder — base '{options.Database}', échelle ×{options.Scale}, {tables.Count} tables, {tables.Sum(t => t.RowCount):N0} lignes à produire");

    await Database.WaitForServerAsync(options.MasterConnectionString, ct);

    if (options.Reset)
    {
        Console.WriteLine("🗑️  --reset : suppression de la base existante");
        await Database.DropAsync(options.MasterConnectionString, options.Database, ct);
    }

    Console.WriteLine("🏗️  Création de la base et des tables (PK uniquement)");
    await Database.CreateDatabaseAsync(options.MasterConnectionString, options.Database, options.Scale, ct);
    await Database.CreateTablesAsync(options.DatabaseConnectionString, ct);

    // Les FK n'existent pas encore => aucun ordre de chargement imposé : on charge plusieurs tables en parallèle.
    var gate = new SemaphoreSlim(options.Parallelism);
    var consoleLock = new object();
    var done = 0;

    void Log(string message) { lock (consoleLock) Console.WriteLine(message); }

    await Task.WhenAll(tables.OrderByDescending(t => t.RowCount).Select(async spec =>
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadTableAsync(spec, options, Log, ct);
            Log($"   ({Interlocked.Increment(ref done)}/{tables.Count} tables terminées)");
        }
        finally { gate.Release(); }
    }));

    if (options.SkipConstraints)
    {
        Console.WriteLine("⏭️  --skip-constraints : index secondaires et FK non créés");
    }
    else
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine("🔗 Création des index secondaires et des clés étrangères (WITH CHECK, peut prendre quelques minutes)…");
        await Database.CreateIndexesAndConstraintsAsync(options.DatabaseConnectionString, ct);
        Console.WriteLine($"   ✔ terminé en {sw.Elapsed:mm\\:ss}");
    }

    Console.WriteLine();
    Console.WriteLine($"{"Table",-16}{"Lignes",15}");
    long sum = 0;
    foreach (var t in tables)
    {
        var count = await Database.CountAsync(options.DatabaseConnectionString, t.Name, ct);
        sum += count;
        Console.WriteLine($"{t.Name,-16}{count,15:N0}");
    }
    Console.WriteLine($"{"TOTAL",-16}{sum,15:N0}");
    Console.WriteLine($"Taille utilisée : {await Database.DatabaseSizeAsync(options.DatabaseConnectionString, ct)}  —  durée totale : {total.Elapsed:hh\\:mm\\:ss}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Annulé. Relancez la commande : les tables complètes seront ignorées.");
    return 130;
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"❌ Erreur SQL : {ex.Message}");
    return 1;
}

static async Task LoadTableAsync(TableSpec spec, Options options, Action<string> log, CancellationToken ct)
{
    var cs = options.DatabaseConnectionString;
    var existing = await Database.CountAsync(cs, spec.Name, ct);

    if (existing == spec.RowCount)
    {
        log($"⏩ {spec.Name,-15} déjà complète ({existing:N0} lignes)");
        return;
    }

    if (existing > 0)
    {
        log($"♻️  {spec.Name,-15} partielle ({existing:N0} lignes) → TRUNCATE puis rechargement");
        await Database.TruncateAsync(cs, spec.Name, ct);   // échoue si des FK existent : utiliser --reset si l'échelle a changé
    }

    var sw = Stopwatch.StartNew();
    log($"📥 {spec.Name,-15} chargement de {spec.RowCount:N0} lignes…");

    await using var cn = new SqlConnection(cs);
    await cn.OpenAsync(ct);

    // TableLock : verrou de table + journalisation minimale. KeepIdentity : on fournit nous-mêmes les Id (références croisées déterministes).
    using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepIdentity, null)
    {
        DestinationTableName = $"dbo.[{spec.Name}]",
        BatchSize = 100_000,
        BulkCopyTimeout = 0,
        EnableStreaming = true,
        NotifyAfter = 1_000_000,
    };
    for (var i = 0; i < spec.Columns.Length; i++)
        bulk.ColumnMappings.Add(i, spec.Columns[i].Name);

    bulk.SqlRowsCopied += (_, e) =>
        log($"   … {spec.Name,-15} {e.RowsCopied,12:N0} / {spec.RowCount:N0}  ({e.RowsCopied / Math.Max(1, sw.Elapsed.TotalSeconds):N0} lignes/s)");

    using var reader = new GeneratedDataReader(spec);
    await bulk.WriteToServerAsync(reader, ct);

    log($"✅ {spec.Name,-15} {spec.RowCount:N0} lignes en {sw.Elapsed:mm\\:ss} ({spec.RowCount / Math.Max(1, sw.Elapsed.TotalSeconds):N0} lignes/s)");
}
