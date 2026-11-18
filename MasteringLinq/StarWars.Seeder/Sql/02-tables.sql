-- Tables + clés primaires uniquement (les FK et index secondaires sont créés APRES le chargement : 03-indexes-and-constraints.sql).
-- Conventions volontairement proches de celles d'EF Core : Id int IDENTITY, enums en int, noms de tables au pluriel.

IF OBJECT_ID(N'dbo.Sectors') IS NULL
CREATE TABLE dbo.Sectors (
    Id          int           NOT NULL IDENTITY(1,1),
    Code        varchar(16)   NOT NULL,
    Name        nvarchar(100) NOT NULL,
    Region      int           NOT NULL,            -- enum Region (Core, InnerRim, MidRim, OuterRim, WildSpace, Unknown)
    DangerLevel int           NOT NULL,            -- 0..10
    CONSTRAINT PK_Sectors PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.StarSystems') IS NULL
CREATE TABLE dbo.StarSystems (
    Id        int           NOT NULL IDENTITY(1,1),
    SectorId  int           NOT NULL,              -- 1-N cascade depuis Sectors
    Name      nvarchar(100) NOT NULL,
    StarType  int           NOT NULL,              -- enum StarType
    X         float         NOT NULL,
    Y         float         NOT NULL,
    Z         float         NOT NULL,
    CONSTRAINT PK_StarSystems PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Planets') IS NULL
CREATE TABLE dbo.Planets (
    Id              int            NOT NULL IDENTITY(1,1),
    StarSystemId    int            NOT NULL,       -- 1-N cascade depuis StarSystems
    Name            nvarchar(100)  NOT NULL,
    Climate         int            NOT NULL,       -- enum Climate
    Terrain         int            NOT NULL,       -- enum Terrain
    DiameterKm      int            NOT NULL,
    Population      bigint         NOT NULL,
    Gravity         decimal(4,2)   NOT NULL,
    HasSpaceport    bit            NOT NULL,
    DiscoveredOn    date           NOT NULL,
    CONSTRAINT PK_Planets PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Species') IS NULL
CREATE TABLE dbo.Species (
    Id               int           NOT NULL IDENTITY(1,1),
    Name             nvarchar(100) NOT NULL,
    Classification   int           NOT NULL,        -- enum Classification
    Language         nvarchar(60)  NOT NULL,
    AverageLifespan  int           NOT NULL,
    AverageHeightCm  decimal(6,1)  NOT NULL,
    IsSentient       bit           NOT NULL,
    HomeworldId      int           NULL,            -- FK optionnelle vers Planets (NO ACTION)
    CONSTRAINT PK_Species PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Factions') IS NULL
CREATE TABLE dbo.Factions (
    Id               int            NOT NULL IDENTITY(1,1),
    ParentFactionId  int            NULL,           -- auto-référence (hiérarchie)
    Name             nvarchar(100)  NOT NULL,
    Alignment        int            NOT NULL,       -- enum Alignment
    FoundedYear      int            NOT NULL,
    Treasury         decimal(18,2)  NOT NULL,
    CONSTRAINT PK_Factions PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Inhabitants') IS NULL
CREATE TABLE dbo.Inhabitants (
    Id                 int            NOT NULL IDENTITY(1,1),
    PlanetId           int            NOT NULL,     -- 1-N cascade depuis Planets
    SpeciesId          int            NOT NULL,     -- NO ACTION
    FactionId          int            NULL,         -- NO ACTION (optionnel)
    FirstName          nvarchar(50)   NOT NULL,
    LastName           nvarchar(50)   NOT NULL,
    BirthYear          int            NOT NULL,
    Gender             int            NOT NULL,
    Credits            decimal(18,2)  NOT NULL,
    MidiChlorians      int            NULL,         -- sensibilité à la Force (NULL = non testé)
    IsActive           bit            NOT NULL,
    CONSTRAINT PK_Inhabitants PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Goods') IS NULL
CREATE TABLE dbo.Goods (
    Id            int            NOT NULL IDENTITY(1,1),
    Sku           varchar(20)    NOT NULL,
    Name          nvarchar(120)  NOT NULL,
    Category      int            NOT NULL,          -- enum GoodCategory
    BasePrice     decimal(18,2)  NOT NULL,
    WeightKg      decimal(10,2)  NOT NULL,
    IsContraband  bit            NOT NULL,
    IsPerishable  bit            NOT NULL,
    CONSTRAINT PK_Goods PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Companies') IS NULL
CREATE TABLE dbo.Companies (
    Id                  int            NOT NULL IDENTITY(1,1),
    HeadquartersId      int            NOT NULL,    -- FK Planets (NO ACTION)
    OwnerInhabitantId   int            NULL,        -- FK Inhabitants (NO ACTION)
    Name                nvarchar(120)  NOT NULL,
    FoundedOn           date           NOT NULL,
    Reputation          int            NOT NULL,    -- -100..100
    Capital             decimal(18,2)  NOT NULL,
    CONSTRAINT PK_Companies PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Starships') IS NULL
CREATE TABLE dbo.Starships (
    Id                 int           NOT NULL IDENTITY(1,1),
    CompanyId          int           NOT NULL,      -- 1-N cascade depuis Companies
    Name               nvarchar(100) NOT NULL,
    Model              nvarchar(60)  NOT NULL,
    CargoCapacity      int           NOT NULL,
    HyperdriveRating   decimal(3,1)  NOT NULL,
    BuiltOn            date          NOT NULL,
    IsOperational      bit           NOT NULL,
    CONSTRAINT PK_Starships PRIMARY KEY CLUSTERED (Id)
);

IF OBJECT_ID(N'dbo.Markets') IS NULL
CREATE TABLE dbo.Markets (
    Id             int           NOT NULL IDENTITY(1,1),
    PlanetId       int           NOT NULL,          -- 1-N cascade depuis Planets
    Name           nvarchar(100) NOT NULL,
    Kind           int           NOT NULL,          -- enum MarketKind
    TaxRate        decimal(5,2)  NOT NULL,
    IsBlackMarket  bit           NOT NULL,
    OpenedOn       date          NOT NULL,
    CONSTRAINT PK_Markets PRIMARY KEY CLUSTERED (Id)
);

-- N-N : Planets <-> Species (avec données portées)
IF OBJECT_ID(N'dbo.PlanetSpecies') IS NULL
CREATE TABLE dbo.PlanetSpecies (
    PlanetId    int    NOT NULL,
    SpeciesId   int    NOT NULL,
    Population  bigint NOT NULL,
    IsNative    bit    NOT NULL,
    CONSTRAINT PK_PlanetSpecies PRIMARY KEY CLUSTERED (PlanetId, SpeciesId)
);

-- N-N : Factions <-> Planets (contrôle / influence)
IF OBJECT_ID(N'dbo.FactionPlanets') IS NULL
CREATE TABLE dbo.FactionPlanets (
    FactionId     int           NOT NULL,
    PlanetId      int           NOT NULL,
    Influence     decimal(5,2)  NOT NULL,
    ControlSince  date          NOT NULL,
    IsCapital     bit           NOT NULL,
    CONSTRAINT PK_FactionPlanets PRIMARY KEY CLUSTERED (FactionId, PlanetId)
);

-- N-N : Markets <-> Goods (stock et prix local)
IF OBJECT_ID(N'dbo.MarketGoods') IS NULL
CREATE TABLE dbo.MarketGoods (
    MarketId   int            NOT NULL,
    GoodId     int            NOT NULL,
    Stock      int            NOT NULL,
    Price      decimal(18,2)  NOT NULL,
    UpdatedAt  datetime2(0)   NOT NULL,
    CONSTRAINT PK_MarketGoods PRIMARY KEY CLUSTERED (MarketId, GoodId)
);

-- Relation TERNAIRE n°1 : Company x Good x Market  (qui a échangé quoi, où) — avec identifiant propre : même triplet répétable dans le temps.
IF OBJECT_ID(N'dbo.Trades') IS NULL
CREATE TABLE dbo.Trades (
    Id          bigint         NOT NULL IDENTITY(1,1),
    MarketId    int            NOT NULL,            -- cascade depuis Markets
    CompanyId   int            NOT NULL,            -- NO ACTION
    GoodId      int            NOT NULL,            -- NO ACTION
    TradeType   int            NOT NULL,            -- 0 = achat, 1 = vente
    Quantity    int            NOT NULL,
    UnitPrice   decimal(18,2)  NOT NULL,
    TradedAt    datetime2(0)   NOT NULL,
    CONSTRAINT PK_Trades PRIMARY KEY CLUSTERED (Id)
);

-- Relation TERNAIRE n°2 : Starship x Good x Market (cargaison chargée à quel marché) — clé composite à 3 colonnes.
IF OBJECT_ID(N'dbo.StarshipCargos') IS NULL
CREATE TABLE dbo.StarshipCargos (
    StarshipId  int           NOT NULL,             -- cascade depuis Starships
    GoodId      int           NOT NULL,             -- NO ACTION
    MarketId    int           NOT NULL,             -- NO ACTION (marché de chargement)
    Quantity    int           NOT NULL,
    LoadedAt    datetime2(0)  NOT NULL,
    CONSTRAINT PK_StarshipCargos PRIMARY KEY CLUSTERED (StarshipId, GoodId, MarketId)
);
