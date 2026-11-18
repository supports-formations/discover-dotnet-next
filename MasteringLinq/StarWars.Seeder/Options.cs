using Microsoft.Data.SqlClient;

namespace StarWars.Seeder;

public sealed class Options
{
    public const string DefaultServerConnection =
        "Server=localhost,1433;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False;Connect Timeout=15";

    public string ServerConnectionString { get; private set; } = DefaultServerConnection;
    public string Database { get; private set; } = "StarWarsTrade";
    public double Scale { get; private set; } = 1.0;
    public int Parallelism { get; private set; } = 4;
    public bool Reset { get; private set; }
    public bool SkipConstraints { get; private set; }
    public bool Help { get; private set; }

    public string DatabaseConnectionString =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = Database }.ConnectionString;

    public string MasterConnectionString =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = "master" }.ConnectionString;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var fromEnv = Environment.GetEnvironmentVariable("SEED_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv)) o.ServerConnectionString = fromEnv;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Valeur manquante pour {args[i]}");
            switch (args[i])
            {
                case "--connection": o.ServerConnectionString = Next(); break;
                case "--database": o.Database = Next(); break;
                case "--scale": o.Scale = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--parallelism": o.Parallelism = Math.Max(1, int.Parse(Next())); break;
                case "--reset": o.Reset = true; break;
                case "--skip-constraints": o.SkipConstraints = true; break;
                case "-h" or "--help": o.Help = true; break;
                default: throw new ArgumentException($"Option inconnue : {args[i]}");
            }
        }

        if (o.Scale <= 0) throw new ArgumentException("--scale doit être > 0");
        return o;
    }

    public const string HelpText = """
        StarWars.Seeder — alimente la base StarWarsTrade (15 tables, ~38 M de lignes à l'échelle 1).

        Usage : dotnet run -c Release --project StarWars.Seeder -- [options]

          --connection <cs>      Chaîne de connexion SERVEUR (sans base). Défaut : sa@localhost,1433
                                 (ou variable d'environnement SEED_CONNECTION)
          --database <nom>       Nom de la base (défaut : StarWarsTrade)
          --scale <x>            Multiplicateur de volume (défaut 1 = >= 1 M de lignes par table ; 0.01 pour un test rapide)
          --parallelism <n>      Nombre de tables chargées simultanément (défaut 4)
          --reset                Supprime la base avant de la recréer
          --skip-constraints     Ne crée pas les index ni les FK après le chargement
          -h, --help             Cette aide

        Le seeder est idempotent : une table déjà complète est ignorée, une table partielle est vidée puis rechargée.
        """;
}
