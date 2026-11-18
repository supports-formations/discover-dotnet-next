-- Exécuté sur master. Paramètre : $(DbName) remplacé par le seeder.
-- Fichiers pré-dimensionnés pour éviter des centaines d'auto-growth pendant le chargement.
IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    CREATE DATABASE [$(DbName)]
    ON PRIMARY (
        NAME = N'$(DbName)_data', FILENAME = N'/var/opt/mssql/data/$(DbName).mdf',
        SIZE = $(DataSizeMb)MB, FILEGROWTH = 1GB)
    LOG ON (
        NAME = N'$(DbName)_log', FILENAME = N'/var/opt/mssql/data/$(DbName)_log.ldf',
        SIZE = $(LogSizeMb)MB, FILEGROWTH = 512MB);

    -- SIMPLE : chargement massif, pas besoin de sauvegarde de journal sur une base de formation.
    ALTER DATABASE [$(DbName)] SET RECOVERY SIMPLE;
END
