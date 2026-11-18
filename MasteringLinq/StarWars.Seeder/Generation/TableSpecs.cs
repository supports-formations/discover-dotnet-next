namespace StarWars.Seeder.Generation;

public sealed record ColumnSpec(string Name, Type Type);

/// <param name="Name">Nom de la table (schéma dbo).</param>
/// <param name="Columns">Colonnes, dans l'ordre exact de remplissage du buffer.</param>
/// <param name="RowCount">Nombre de lignes à générer.</param>
/// <param name="Fill">Remplit le buffer pour la ligne 0-based i.</param>
/// <param name="HasIdentity">True si la 1re colonne est une IDENTITY (insérée telle quelle via KeepIdentity).</param>
public sealed record TableSpec(string Name, ColumnSpec[] Columns, long RowCount, Action<long, object?[]> Fill, bool HasIdentity);

public static class TableSpecs
{
    private static readonly DateTime Epoch = new(1990, 1, 1);

    /// <summary>Volumes de référence (scale = 1) : tous >= 1 million de lignes.</summary>
    public sealed record Counts(double Scale)
    {
        private long N(long baseCount) => Math.Max(1, (long)(baseCount * Scale));

        public long Sectors => N(1_000_000);
        public long StarSystems => N(2_000_000);
        public long Planets => N(3_000_000);
        public long Species => N(1_000_000);
        public long Factions => N(1_000_000);
        public long Inhabitants => N(4_000_000);
        public long Goods => N(1_000_000);
        public long Companies => N(1_000_000);
        public long Starships => N(2_000_000);
        public long Markets => N(2_000_000);
        public long PlanetSpecies => N(5_000_000);
        public long FactionPlanets => N(2_000_000);
        public long MarketGoods => N(4_000_000);
        public long Trades => N(6_000_000);
        public long StarshipCargos => N(3_000_000);
    }

    private static ulong SeedOf(string table)
    {
        // hash FNV-1a stable (string.GetHashCode est randomisé par process)
        var h = 14695981039346656037UL;
        foreach (var c in table) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static ColumnSpec C<T>(string name) => new(name, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));

    public static IReadOnlyList<TableSpec> Build(double scale)
    {
        var n = new Counts(scale);
        var tables = new List<TableSpec>();

        // ---------------------------------------------------------------- Sectors
        var seedSectors = SeedOf("Sectors");
        tables.Add(new("Sectors",
            [C<int>("Id"), C<string>("Code"), C<string>("Name"), C<int>("Region"), C<int>("DangerLevel")],
            n.Sectors,
            (i, b) =>
            {
                var r = new Rng(i, seedSectors);
                var id = i + 1;
                b[0] = (int)id;
                b[1] = $"SEC-{id:D8}";
                b[2] = Names.Sector(r, id);
                b[3] = r.Int(1, 6);
                b[4] = r.Between(2, 0, 10);
            }, true));

        // ---------------------------------------------------------------- StarSystems (N par secteur)
        var seedStarSystems = SeedOf("StarSystems");
        tables.Add(new("StarSystems",
            [C<int>("Id"), C<int>("SectorId"), C<string>("Name"), C<int>("StarType"), C<double>("X"), C<double>("Y"), C<double>("Z")],
            n.StarSystems,
            (i, b) =>
            {
                var r = new Rng(i, seedStarSystems);
                b[0] = (int)(i + 1);
                b[1] = Spread.Parent(i, n.StarSystems, n.Sectors, out _);
                b[2] = Names.StarSystem(r, i + 1);
                b[3] = r.Int(1, 8);
                b[4] = r.Between(2, -50_000, 50_000) / 10.0;
                b[5] = r.Between(3, -50_000, 50_000) / 10.0;
                b[6] = r.Between(4, -5_000, 5_000) / 10.0;
            }, true));

        // ---------------------------------------------------------------- Planets (N par système)
        var seedPlanets = SeedOf("Planets");
        tables.Add(new("Planets",
            [C<int>("Id"), C<int>("StarSystemId"), C<string>("Name"), C<int>("Climate"), C<int>("Terrain"), C<int>("DiameterKm"),
             C<long>("Population"), C<decimal>("Gravity"), C<bool>("HasSpaceport"), C<DateTime>("DiscoveredOn")],
            n.Planets,
            (i, b) =>
            {
                var r = new Rng(i, seedPlanets);
                b[0] = (int)(i + 1);
                b[1] = Spread.Parent(i, n.Planets, n.StarSystems, out _);
                b[2] = Names.Planet(r);
                b[3] = r.Int(1, 9);
                b[4] = r.Int(2, 11);
                b[5] = r.Between(3, 800, 200_000);
                b[6] = r.Between(4, 1, 999) * (long)Math.Pow(10, r.Int(5, 9));   // distribution très asymétrique : quelques géantes, beaucoup de petites
                b[7] = r.Between(6, 10, 350) / 100m;
                b[8] = r.Chance(7, 35);
                b[9] = r.Date(8, Epoch, 12_000);
            }, true));

        // ---------------------------------------------------------------- Species
        var seedSpecies = SeedOf("Species");
        tables.Add(new("Species",
            [C<int>("Id"), C<string>("Name"), C<int>("Classification"), C<string>("Language"), C<int>("AverageLifespan"),
             C<decimal>("AverageHeightCm"), C<bool>("IsSentient"), C<int?>("HomeworldId")],
            n.Species,
            (i, b) =>
            {
                var r = new Rng(i, seedSpecies);
                b[0] = (int)(i + 1);
                b[1] = Names.Species(r);
                b[2] = r.Int(1, 7);
                b[3] = Names.Language(r, 2);
                b[4] = r.Between(3, 5, 900);
                b[5] = r.Between(4, 30, 3000) / 10m;
                b[6] = r.Chance(5, 85);
                b[7] = r.Chance(6, 90) ? r.Id(7, n.Planets) : null;
            }, true));

        // ---------------------------------------------------------------- Factions (auto-référence)
        var seedFactions = SeedOf("Factions");
        tables.Add(new("Factions",
            [C<int>("Id"), C<int?>("ParentFactionId"), C<string>("Name"), C<int>("Alignment"), C<int>("FoundedYear"), C<decimal>("Treasury")],
            n.Factions,
            (i, b) =>
            {
                var r = new Rng(i, seedFactions);
                b[0] = (int)(i + 1);
                b[1] = i > 0 && r.Chance(1, 40) ? r.Id(2, i) : null;     // parent toujours "plus ancien" (id inférieur)
                b[2] = Names.Faction(r);
                b[3] = r.Int(3, 5);
                b[4] = r.Between(4, -25_000, 35);                         // en BBY/ABY (négatif = avant la bataille de Yavin)
                b[5] = r.Money(5, 0, 5_000_000);
            }, true));

        // ---------------------------------------------------------------- Inhabitants
        var seedInhabitants = SeedOf("Inhabitants");
        tables.Add(new("Inhabitants",
            [C<int>("Id"), C<int>("PlanetId"), C<int>("SpeciesId"), C<int?>("FactionId"), C<string>("FirstName"), C<string>("LastName"),
             C<int>("BirthYear"), C<int>("Gender"), C<decimal>("Credits"), C<int?>("MidiChlorians"), C<bool>("IsActive")],
            n.Inhabitants,
            (i, b) =>
            {
                var r = new Rng(i, seedInhabitants);
                b[0] = (int)(i + 1);
                b[1] = Spread.Parent(i, n.Inhabitants, n.Planets, out _);
                b[2] = r.Id(2, n.Species);
                b[3] = r.Chance(3, 70) ? r.Id(4, n.Factions) : null;
                b[4] = Names.FirstName(r);
                b[5] = Names.LastName(r);
                b[6] = r.Between(6, -200, 40);
                b[7] = r.Int(7, 3);
                b[8] = r.Money(8, 0, 250_000);
                b[9] = r.Chance(9, 25) ? r.Between(10, 2_000, 25_000) : null;
                b[10] = r.Chance(11, 92);
            }, true));

        // ---------------------------------------------------------------- Goods
        var seedGoods = SeedOf("Goods");
        tables.Add(new("Goods",
            [C<int>("Id"), C<string>("Sku"), C<string>("Name"), C<int>("Category"), C<decimal>("BasePrice"), C<decimal>("WeightKg"),
             C<bool>("IsContraband"), C<bool>("IsPerishable")],
            n.Goods,
            (i, b) =>
            {
                var r = new Rng(i, seedGoods);
                b[0] = (int)(i + 1);
                b[1] = $"GD-{i + 1:D10}";
                b[2] = Names.Good(r);
                b[3] = r.Int(2, 10);
                b[4] = r.Money(3, 1, 100_000);
                b[5] = r.Between(4, 1, 5_000_000) / 100m;
                b[6] = r.Chance(5, 8);
                b[7] = r.Chance(6, 20);
            }, true));

        // ---------------------------------------------------------------- Companies
        var seedCompanies = SeedOf("Companies");
        tables.Add(new("Companies",
            [C<int>("Id"), C<int>("HeadquartersId"), C<int?>("OwnerInhabitantId"), C<string>("Name"), C<DateTime>("FoundedOn"),
             C<int>("Reputation"), C<decimal>("Capital")],
            n.Companies,
            (i, b) =>
            {
                var r = new Rng(i, seedCompanies);
                b[0] = (int)(i + 1);
                b[1] = r.Id(2, n.Planets);
                b[2] = r.Chance(3, 80) ? r.Id(4, n.Inhabitants) : null;
                b[3] = Names.Company(r);
                b[4] = r.Date(5, Epoch, 11_000);
                b[5] = r.Between(6, -100, 100);
                b[6] = r.Money(7, 1_000, 900_000_000);
            }, true));

        // ---------------------------------------------------------------- Starships (N par compagnie)
        var seedStarships = SeedOf("Starships");
        tables.Add(new("Starships",
            [C<int>("Id"), C<int>("CompanyId"), C<string>("Name"), C<string>("Model"), C<int>("CargoCapacity"),
             C<decimal>("HyperdriveRating"), C<DateTime>("BuiltOn"), C<bool>("IsOperational")],
            n.Starships,
            (i, b) =>
            {
                var r = new Rng(i, seedStarships);
                b[0] = (int)(i + 1);
                b[1] = Spread.Parent(i, n.Starships, n.Companies, out _);
                b[2] = Names.Starship(r);
                b[3] = Names.ShipModel(r);
                b[4] = r.Between(2, 10, 100_000);
                b[5] = r.Between(3, 5, 40) / 10m;
                b[6] = r.Date(4, Epoch, 11_000);
                b[7] = r.Chance(5, 88);
            }, true));

        // ---------------------------------------------------------------- Markets (N par planète)
        var seedMarkets = SeedOf("Markets");
        tables.Add(new("Markets",
            [C<int>("Id"), C<int>("PlanetId"), C<string>("Name"), C<int>("Kind"), C<decimal>("TaxRate"), C<bool>("IsBlackMarket"), C<DateTime>("OpenedOn")],
            n.Markets,
            (i, b) =>
            {
                var r = new Rng(i, seedMarkets);
                b[0] = (int)(i + 1);
                b[1] = Spread.Parent(i, n.Markets, n.Planets, out _);
                b[2] = Names.Market(r);
                b[3] = r.Int(2, 6);
                b[4] = r.Between(3, 0, 2_500) / 100m;
                b[5] = r.Chance(4, 6);
                b[6] = r.Date(5, Epoch, 11_000);
            }, true));

        // ---------------------------------------------------------------- PlanetSpecies (N-N)
        var seedPlanetSpecies = SeedOf("PlanetSpecies");
        tables.Add(new("PlanetSpecies",
            [C<int>("PlanetId"), C<int>("SpeciesId"), C<long>("Population"), C<bool>("IsNative")],
            n.PlanetSpecies,
            (i, b) =>
            {
                var r = new Rng(i, seedPlanetSpecies);
                var planet = Spread.Parent(i, n.PlanetSpecies, n.Planets, out var k);
                b[0] = planet;
                b[1] = Spread.Other(planet, k, n.Species);
                b[2] = r.Between(2, 1, 999) * (long)Math.Pow(10, r.Int(3, 8));
                b[3] = r.Chance(4, 40);
            }, false));

        // ---------------------------------------------------------------- FactionPlanets (N-N)
        var seedFactionPlanets = SeedOf("FactionPlanets");
        tables.Add(new("FactionPlanets",
            [C<int>("FactionId"), C<int>("PlanetId"), C<decimal>("Influence"), C<DateTime>("ControlSince"), C<bool>("IsCapital")],
            n.FactionPlanets,
            (i, b) =>
            {
                var r = new Rng(i, seedFactionPlanets);
                var faction = Spread.Parent(i, n.FactionPlanets, n.Factions, out var k);
                b[0] = faction;
                b[1] = Spread.Other(faction, k, n.Planets);
                b[2] = r.Between(2, 1, 10_000) / 100m;
                b[3] = r.Date(3, Epoch, 11_000);
                b[4] = r.Chance(4, 3);
            }, false));

        // ---------------------------------------------------------------- MarketGoods (N-N)
        var seedMarketGoods = SeedOf("MarketGoods");
        tables.Add(new("MarketGoods",
            [C<int>("MarketId"), C<int>("GoodId"), C<int>("Stock"), C<decimal>("Price"), C<DateTime>("UpdatedAt")],
            n.MarketGoods,
            (i, b) =>
            {
                var r = new Rng(i, seedMarketGoods);
                var market = Spread.Parent(i, n.MarketGoods, n.Markets, out var k);
                b[0] = market;
                b[1] = Spread.Other(market, k, n.Goods);
                b[2] = r.Between(2, 0, 100_000);
                b[3] = r.Money(3, 1, 150_000);
                b[4] = new DateTime(2026, 1, 1).AddSeconds(r.Int(4, 86_400 * 300));
            }, false));

        // ---------------------------------------------------------------- Trades (ternaire Company x Good x Market, série temporelle)
        var seedTrades = SeedOf("Trades");
        var tradeStart = new DateTime(2023, 1, 1);
        const long threeYearsSeconds = 3L * 365 * 86_400;
        tables.Add(new("Trades",
            [C<long>("Id"), C<int>("MarketId"), C<int>("CompanyId"), C<int>("GoodId"), C<int>("TradeType"), C<int>("Quantity"),
             C<decimal>("UnitPrice"), C<DateTime>("TradedAt")],
            n.Trades,
            (i, b) =>
            {
                var r = new Rng(i, seedTrades);
                b[0] = i + 1;
                b[1] = r.Id(1, n.Markets);
                b[2] = r.Id(2, n.Companies);
                b[3] = r.Id(3, n.Goods);
                b[4] = r.Int(4, 2);
                b[5] = r.Between(5, 1, 500);
                b[6] = r.Money(6, 1, 50_000);
                b[7] = tradeStart.AddSeconds(i * threeYearsSeconds / n.Trades);   // croissant avec l'Id, comme un vrai journal
            }, true));

        // ---------------------------------------------------------------- StarshipCargos (ternaire Starship x Good x Market)
        var seedStarshipCargos = SeedOf("StarshipCargos");
        tables.Add(new("StarshipCargos",
            [C<int>("StarshipId"), C<int>("GoodId"), C<int>("MarketId"), C<int>("Quantity"), C<DateTime>("LoadedAt")],
            n.StarshipCargos,
            (i, b) =>
            {
                var r = new Rng(i, seedStarshipCargos);
                var ship = Spread.Parent(i, n.StarshipCargos, n.Starships, out var k);
                b[0] = ship;
                b[1] = Spread.Other(ship, k, n.Goods);
                b[2] = r.Id(2, n.Markets);
                b[3] = r.Between(3, 1, 20_000);
                b[4] = new DateTime(2026, 1, 1).AddSeconds(r.Int(4, 86_400 * 300));
            }, false));

        return tables;
    }
}
