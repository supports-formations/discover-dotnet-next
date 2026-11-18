-- Exécuté APRES le chargement massif (index + FK construits d'un coup = beaucoup plus rapide, et FK "trusted" grâce à WITH CHECK).
-- Idempotent : relançable sans erreur.
-- Les index suivent ce que créerait EF Core par convention (un index par FK) + 2 index uniques métier.
-- Aucun index "de performance" supplémentaire : ce sera l'objet des ateliers suivants.

------------------------------------------------------------------ Index
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sectors_Code')
    CREATE UNIQUE INDEX UX_Sectors_Code ON dbo.Sectors (Code);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Goods_Sku')
    CREATE UNIQUE INDEX UX_Goods_Sku ON dbo.Goods (Sku);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StarSystems_SectorId')
    CREATE INDEX IX_StarSystems_SectorId ON dbo.StarSystems (SectorId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Planets_StarSystemId')
    CREATE INDEX IX_Planets_StarSystemId ON dbo.Planets (StarSystemId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Species_HomeworldId')
    CREATE INDEX IX_Species_HomeworldId ON dbo.Species (HomeworldId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Factions_ParentFactionId')
    CREATE INDEX IX_Factions_ParentFactionId ON dbo.Factions (ParentFactionId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Inhabitants_PlanetId')
    CREATE INDEX IX_Inhabitants_PlanetId ON dbo.Inhabitants (PlanetId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Inhabitants_SpeciesId')
    CREATE INDEX IX_Inhabitants_SpeciesId ON dbo.Inhabitants (SpeciesId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Inhabitants_FactionId')
    CREATE INDEX IX_Inhabitants_FactionId ON dbo.Inhabitants (FactionId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Companies_HeadquartersId')
    CREATE INDEX IX_Companies_HeadquartersId ON dbo.Companies (HeadquartersId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Companies_OwnerInhabitantId')
    CREATE INDEX IX_Companies_OwnerInhabitantId ON dbo.Companies (OwnerInhabitantId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Starships_CompanyId')
    CREATE INDEX IX_Starships_CompanyId ON dbo.Starships (CompanyId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Markets_PlanetId')
    CREATE INDEX IX_Markets_PlanetId ON dbo.Markets (PlanetId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlanetSpecies_SpeciesId')
    CREATE INDEX IX_PlanetSpecies_SpeciesId ON dbo.PlanetSpecies (SpeciesId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FactionPlanets_PlanetId')
    CREATE INDEX IX_FactionPlanets_PlanetId ON dbo.FactionPlanets (PlanetId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MarketGoods_GoodId')
    CREATE INDEX IX_MarketGoods_GoodId ON dbo.MarketGoods (GoodId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trades_MarketId')
    CREATE INDEX IX_Trades_MarketId ON dbo.Trades (MarketId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trades_CompanyId')
    CREATE INDEX IX_Trades_CompanyId ON dbo.Trades (CompanyId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trades_GoodId')
    CREATE INDEX IX_Trades_GoodId ON dbo.Trades (GoodId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StarshipCargos_GoodId')
    CREATE INDEX IX_StarshipCargos_GoodId ON dbo.StarshipCargos (GoodId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StarshipCargos_MarketId')
    CREATE INDEX IX_StarshipCargos_MarketId ON dbo.StarshipCargos (MarketId);

------------------------------------------------------------------ Clés étrangères (WITH CHECK => trusted)
-- Chaîne en CASCADE : Sectors -> StarSystems -> Planets -> (Inhabitants | Markets | PlanetSpecies | FactionPlanets)
--                     Markets -> (MarketGoods | Trades) ; Companies -> Starships -> StarshipCargos
-- Tout le reste en NO ACTION (SQL Server interdit les chemins de cascade multiples).

IF OBJECT_ID(N'FK_StarSystems_Sectors_SectorId', N'F') IS NULL
    ALTER TABLE dbo.StarSystems WITH CHECK ADD CONSTRAINT FK_StarSystems_Sectors_SectorId
        FOREIGN KEY (SectorId) REFERENCES dbo.Sectors (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_Planets_StarSystems_StarSystemId', N'F') IS NULL
    ALTER TABLE dbo.Planets WITH CHECK ADD CONSTRAINT FK_Planets_StarSystems_StarSystemId
        FOREIGN KEY (StarSystemId) REFERENCES dbo.StarSystems (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_Species_Planets_HomeworldId', N'F') IS NULL
    ALTER TABLE dbo.Species WITH CHECK ADD CONSTRAINT FK_Species_Planets_HomeworldId
        FOREIGN KEY (HomeworldId) REFERENCES dbo.Planets (Id);
IF OBJECT_ID(N'FK_Factions_Factions_ParentFactionId', N'F') IS NULL
    ALTER TABLE dbo.Factions WITH CHECK ADD CONSTRAINT FK_Factions_Factions_ParentFactionId
        FOREIGN KEY (ParentFactionId) REFERENCES dbo.Factions (Id);
IF OBJECT_ID(N'FK_Inhabitants_Planets_PlanetId', N'F') IS NULL
    ALTER TABLE dbo.Inhabitants WITH CHECK ADD CONSTRAINT FK_Inhabitants_Planets_PlanetId
        FOREIGN KEY (PlanetId) REFERENCES dbo.Planets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_Inhabitants_Species_SpeciesId', N'F') IS NULL
    ALTER TABLE dbo.Inhabitants WITH CHECK ADD CONSTRAINT FK_Inhabitants_Species_SpeciesId
        FOREIGN KEY (SpeciesId) REFERENCES dbo.Species (Id);
IF OBJECT_ID(N'FK_Inhabitants_Factions_FactionId', N'F') IS NULL
    ALTER TABLE dbo.Inhabitants WITH CHECK ADD CONSTRAINT FK_Inhabitants_Factions_FactionId
        FOREIGN KEY (FactionId) REFERENCES dbo.Factions (Id);
IF OBJECT_ID(N'FK_Companies_Planets_HeadquartersId', N'F') IS NULL
    ALTER TABLE dbo.Companies WITH CHECK ADD CONSTRAINT FK_Companies_Planets_HeadquartersId
        FOREIGN KEY (HeadquartersId) REFERENCES dbo.Planets (Id);
IF OBJECT_ID(N'FK_Companies_Inhabitants_OwnerInhabitantId', N'F') IS NULL
    ALTER TABLE dbo.Companies WITH CHECK ADD CONSTRAINT FK_Companies_Inhabitants_OwnerInhabitantId
        FOREIGN KEY (OwnerInhabitantId) REFERENCES dbo.Inhabitants (Id);
IF OBJECT_ID(N'FK_Starships_Companies_CompanyId', N'F') IS NULL
    ALTER TABLE dbo.Starships WITH CHECK ADD CONSTRAINT FK_Starships_Companies_CompanyId
        FOREIGN KEY (CompanyId) REFERENCES dbo.Companies (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_Markets_Planets_PlanetId', N'F') IS NULL
    ALTER TABLE dbo.Markets WITH CHECK ADD CONSTRAINT FK_Markets_Planets_PlanetId
        FOREIGN KEY (PlanetId) REFERENCES dbo.Planets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_PlanetSpecies_Planets_PlanetId', N'F') IS NULL
    ALTER TABLE dbo.PlanetSpecies WITH CHECK ADD CONSTRAINT FK_PlanetSpecies_Planets_PlanetId
        FOREIGN KEY (PlanetId) REFERENCES dbo.Planets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_PlanetSpecies_Species_SpeciesId', N'F') IS NULL
    ALTER TABLE dbo.PlanetSpecies WITH CHECK ADD CONSTRAINT FK_PlanetSpecies_Species_SpeciesId
        FOREIGN KEY (SpeciesId) REFERENCES dbo.Species (Id);
IF OBJECT_ID(N'FK_FactionPlanets_Planets_PlanetId', N'F') IS NULL
    ALTER TABLE dbo.FactionPlanets WITH CHECK ADD CONSTRAINT FK_FactionPlanets_Planets_PlanetId
        FOREIGN KEY (PlanetId) REFERENCES dbo.Planets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_FactionPlanets_Factions_FactionId', N'F') IS NULL
    ALTER TABLE dbo.FactionPlanets WITH CHECK ADD CONSTRAINT FK_FactionPlanets_Factions_FactionId
        FOREIGN KEY (FactionId) REFERENCES dbo.Factions (Id);
IF OBJECT_ID(N'FK_MarketGoods_Markets_MarketId', N'F') IS NULL
    ALTER TABLE dbo.MarketGoods WITH CHECK ADD CONSTRAINT FK_MarketGoods_Markets_MarketId
        FOREIGN KEY (MarketId) REFERENCES dbo.Markets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_MarketGoods_Goods_GoodId', N'F') IS NULL
    ALTER TABLE dbo.MarketGoods WITH CHECK ADD CONSTRAINT FK_MarketGoods_Goods_GoodId
        FOREIGN KEY (GoodId) REFERENCES dbo.Goods (Id);
IF OBJECT_ID(N'FK_Trades_Markets_MarketId', N'F') IS NULL
    ALTER TABLE dbo.Trades WITH CHECK ADD CONSTRAINT FK_Trades_Markets_MarketId
        FOREIGN KEY (MarketId) REFERENCES dbo.Markets (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_Trades_Companies_CompanyId', N'F') IS NULL
    ALTER TABLE dbo.Trades WITH CHECK ADD CONSTRAINT FK_Trades_Companies_CompanyId
        FOREIGN KEY (CompanyId) REFERENCES dbo.Companies (Id);
IF OBJECT_ID(N'FK_Trades_Goods_GoodId', N'F') IS NULL
    ALTER TABLE dbo.Trades WITH CHECK ADD CONSTRAINT FK_Trades_Goods_GoodId
        FOREIGN KEY (GoodId) REFERENCES dbo.Goods (Id);
IF OBJECT_ID(N'FK_StarshipCargos_Starships_StarshipId', N'F') IS NULL
    ALTER TABLE dbo.StarshipCargos WITH CHECK ADD CONSTRAINT FK_StarshipCargos_Starships_StarshipId
        FOREIGN KEY (StarshipId) REFERENCES dbo.Starships (Id) ON DELETE CASCADE;
IF OBJECT_ID(N'FK_StarshipCargos_Goods_GoodId', N'F') IS NULL
    ALTER TABLE dbo.StarshipCargos WITH CHECK ADD CONSTRAINT FK_StarshipCargos_Goods_GoodId
        FOREIGN KEY (GoodId) REFERENCES dbo.Goods (Id);
IF OBJECT_ID(N'FK_StarshipCargos_Markets_MarketId', N'F') IS NULL
    ALTER TABLE dbo.StarshipCargos WITH CHECK ADD CONSTRAINT FK_StarshipCargos_Markets_MarketId
        FOREIGN KEY (MarketId) REFERENCES dbo.Markets (Id);
