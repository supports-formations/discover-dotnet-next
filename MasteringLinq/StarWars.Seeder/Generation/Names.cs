namespace StarWars.Seeder.Generation;

/// <summary>Banque de syllabes / mots pour fabriquer des noms "Star Wars" crédibles.</summary>
public static class Names
{
    private static readonly string[] Starts =
    [
        "Tat", "Cor", "Nab", "Kash", "Hot", "Dag", "Ala", "Man", "Cor", "Jak", "Mus", "End", "Yav", "Dan", "Ord",
        "Kam", "Bes", "Ryl", "Sul", "Tyth", "Mon", "Ker", "Lot", "Bar", "Nal", "Ziost", "Ilu", "Vur", "Ryd", "Gam",
    ];

    private static readonly string[] Middles =
    [
        "oo", "us", "an", "or", "ar", "ev", "ul", "ak", "in", "es", "ot", "ib", "ur", "al", "eth", "ond", "ira", "ex",
    ];

    private static readonly string[] Ends =
    [
        "ine", "ia", "is", "on", "ar", "ooine", "ba", "ii", "ath", "ura", "ris", "tos", "dar", "nor", "vek", "ona",
        "bin", "lan", "pel", "zan", "kor", "ine", "tuin", "bar",
    ];

    private static readonly string[] FirstNames =
    [
        "Luke", "Leia", "Han", "Anakin", "Obi", "Padmé", "Qui", "Mace", "Yoda", "Ahsoka", "Rex", "Cassian", "Jyn", "Din",
        "Boba", "Lando", "Wedge", "Biggs", "Poe", "Finn", "Rey", "Kylo", "Mara", "Thrawn", "Ezra", "Hera", "Kanan",
        "Sabine", "Zeb", "Cad", "Bail", "Mon", "Shaak", "Plo", "Kit", "Aayla", "Quinlan", "Barriss", "Nute", "Wat",
    ];

    private static readonly string[] LastNames =
    [
        "Skywalker", "Organa", "Solo", "Kenobi", "Jinn", "Windu", "Amidala", "Tano", "Andor", "Erso", "Djarin", "Fett",
        "Calrissian", "Antilles", "Dameron", "Ren", "Jade", "Syndulla", "Jarrus", "Wren", "Orrelios", "Bane", "Mothma",
        "Fisto", "Koon", "Secura", "Ventress", "Offee", "Dooku", "Maul", "Tarkin", "Krennic", "Piett", "Veers",
    ];

    private static readonly string[] StarSuffix = ["Prime", "Minor", "Major", "Reach", "Gate", "Drift", "Expanse", "Cluster", "Verge", "Deep"];
    private static readonly string[] ShipModels =
    [
        "YT-1300", "YT-2400", "GR-75", "Lambda Shuttle", "Firespray-31", "Nebulon-B", "Corellian Corvette", "Ghtroc 720",
        "Kuat Cargo Hauler", "Corellian Gunship", "Hammerhead", "Sorosuub Yacht", "Z-95 Headhunter", "Y-Wing", "U-Wing",
    ];

    private static readonly string[] GoodAdjectives =
    [
        "Refined", "Raw", "Crystalline", "Imperial", "Rebel", "Wookiee", "Corellian", "Mandalorian", "Kyber", "Tibanna",
        "Spice", "Synthetic", "Ancient", "Salvaged", "Forged", "Illegal", "Premium", "Standard", "Bulk", "Rare",
    ];

    private static readonly string[] GoodNouns =
    [
        "Beskar", "Durasteel", "Blaster Parts", "Holocron", "Hyperfuel", "Spice", "Bacta", "Ale", "Droid Core", "Ration Pack",
        "Lightsaber Crystal", "Transparisteel", "Carbonite", "Thermal Detonator", "Medpac", "Navicomputer", "Power Cell",
        "Moisture Vaporator", "Speeder Engine", "Shield Generator", "Coaxium", "Kaf", "Nerf Meat", "Fabric", "Ore",
    ];

    private static readonly string[] FactionKinds =
    [
        "Alliance", "Empire", "Republic", "Syndicate", "Guild", "Clan", "Order", "Federation", "Consortium", "Cartel",
        "Council", "Brotherhood", "Collective", "Dominion", "Legion",
    ];

    private static readonly string[] Languages =
    [
        "Basic", "Huttese", "Shyriiwook", "Ewokese", "Mando'a", "Rodese", "Bocce", "Sith", "Gungan", "Twi'leki", "Togruti", "Zabraki",
    ];

    private static readonly string[] MarketKinds = ["Bazaar", "Spaceport Dock", "Guild Hall", "Black Market", "Cantina Exchange", "Orbital Station"];

    public static string Place(Rng r, int salt) =>
        r.Pick(salt, Starts) + r.Pick(salt + 1, Middles) + r.Pick(salt + 2, Ends);

    public static string Sector(Rng r, long id) => $"{Place(r, 10)} Sector {id}";
    public static string StarSystem(Rng r, long id) => $"{Place(r, 10)} {r.Pick(13, StarSuffix)} {id % 997}";
    public static string Planet(Rng r) => Place(r, 10) + (r.Chance(14, 30) ? " " + r.Pick(15, StarSuffix) : "");
    public static string Species(Rng r) => Place(r, 10) + (r.Chance(14, 50) ? "ian" : "ese");
    public static string Language(Rng r, int salt) => r.Pick(salt, Languages);
    public static string FirstName(Rng r) => r.Pick(20, FirstNames);
    public static string LastName(Rng r) => r.Pick(21, LastNames);
    public static string Faction(Rng r) => $"{r.Pick(30, FactionKinds)} of {Place(r, 31)}";
    public static string Company(Rng r) => $"{Place(r, 40)} {r.Pick(43, new[] { "Trading", "Freight", "Logistics", "Mining", "Industries", "Exports", "Holdings" })}";
    public static string Good(Rng r) => $"{r.Pick(50, GoodAdjectives)} {r.Pick(51, GoodNouns)}";
    public static string Starship(Rng r) => $"{r.Pick(60, new[] { "Millennium", "Ghost", "Slave", "Razor", "Outrider", "Tantive", "Lucky", "Rogue", "Silent", "Crimson" })} {r.Pick(61, new[] { "Falcon", "Star", "Wing", "Comet", "Runner", "Hauler", "Drifter", "Spark", "Wanderer", "Fury" })}";
    public static string ShipModel(Rng r) => r.Pick(62, ShipModels);
    public static string Market(Rng r) => $"{Place(r, 70)} {r.Pick(73, MarketKinds)}";
}
