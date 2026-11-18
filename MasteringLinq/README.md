# MasteringLinq — Workshop EF Core (.NET 10/11)

Workshop avancé : tirer le maximum d'EF Core et de LINQ sur un jeu de données de **38 millions de lignes**
(univers Star Wars : planètes, habitants, espèces, commerce galactique).

## Étape 01 — Base de données

| Fichier | Rôle |
|---------|------|
| [`docker-compose.yml`](docker-compose.yml) | SQL Server 2022 (Developer) dans Docker, volume persistant, healthcheck |
| [`docs/01-mcd.md`](docs/01-mcd.md) | MCD Mermaid des 15 tables + règles de cascade + choix de conception |
| [`StarWars.Seeder/`](StarWars.Seeder) | Console C# qui crée le schéma et charge les données (`SqlBulkCopy` en streaming) |

### Lancer

```bash
# 1. Démarrer SQL Server (premier lancement : télécharge l'image ~1,5 Go)
docker compose up -d

# 2. Créer la base et la remplir (≈ 38 M de lignes en ~1 min sur une machine récente ; ~3 Go de données ; relançable sans risque)
dotnet run -c Release --project StarWars.Seeder
```

Options utiles du seeder :

```bash
dotnet run -c Release --project StarWars.Seeder -- --scale 0.01   # test rapide : ~380 000 lignes
dotnet run -c Release --project StarWars.Seeder -- --reset        # repart de zéro
dotnet run -c Release --project StarWars.Seeder -- --help
```

### Connexion

```
Server=localhost,1433;Database=StarWarsTrade;User Id=sa;Password=StarWars_Pwd123!;TrustServerCertificate=True;Encrypt=False
```

Le mot de passe, le port et la RAM de SQL Server se surchargent via les variables `MSSQL_SA_PASSWORD`, `MSSQL_PORT`,
`MSSQL_MEMORY_LIMIT_MB` (fichier `.env` à côté du `docker-compose.yml`). Côté seeder : variable `SEED_CONNECTION` ou `--connection`.

### Arrêter / réinitialiser

```bash
docker compose stop          # conserve les données
docker compose down -v       # supprime aussi le volume (~8 Go libérés, il faudra reseeder)
```

### Choix techniques

- **Pas d'EF Core dans le seeder** : le schéma est du SQL pur (`StarWars.Seeder/Sql/`). Le modèle EF sera écrit dans les ateliers, sans être biaisé par un générateur.
- **Chargement rapide** : tables + PK d'abord, données en `SqlBulkCopy` (4 tables en parallèle, lignes générées à la volée, zéro `DataTable`), puis index secondaires et FK `WITH CHECK` (FK *trusted*, ce qui compte pour l'optimiseur).
- **Déterministe et idempotent** : même base à chaque exécution ; une table complète est ignorée, une table partielle est rechargée.
