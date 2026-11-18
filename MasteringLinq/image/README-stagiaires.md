# Base StarWarsTrade (SQL Server 2022, 38 M de lignes)

Prérequis : Docker, ~10 Go libres, 4 Go de RAM minimum alloués à Docker.

```bash
docker run -d --name starwars-sql -p 1433:1433 -e MSSQL_MEMORY_LIMIT_MB=4096 ghcr.io/supports-formations/masteringlinq-db:1.0
docker logs -f starwars-sql      # attendre ">>> StarWarsTrade prête" (~1 min au 1er démarrage)
```

Le premier lancement télécharge l'image (~1,5 Go), puis restaure la base automatiquement.

Connexion :
`Server=localhost,1433;Database=StarWarsTrade;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False`

Arrêter / relancer : `docker stop starwars-sql` / `docker start starwars-sql` (les données sont conservées).

Repartir de zéro : `docker rm -f starwars-sql` puis relancer le `docker run`.

Port 1433 déjà utilisé ? Remplacer `-p 1433:1433` par `-p 14330:1433` et adapter la chaîne de connexion (`localhost,14330`).
