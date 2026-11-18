public enum Resource { Minerai, Cristal, Vivres, Carburant, Credits }
public enum PlanetType { Aride, Oceanique, Volcanique, Forestiere, CiteMonde, Glaciaire }
public enum FactionCode { CSL, KSY, VHR, CLB, Neutre }
public enum FleetStatus { EnOrbite, EnTransit, AuCombat, Detruite }

public sealed class DomainException(string message) : Exception(message);

public readonly record struct Coordinates(double X, double Y, double Z)
{
    public double DistanceTo(Coordinates other)
    {
        var dx = X - other.X; var dy = Y - other.Y; var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public readonly record struct PlanetCode(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ResourceStock(Resource Resource, long Quantity);

public sealed class Ship
{
    public Ship(string shipClass, int cargoCapacity, int firepower, int maxHull, double speed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shipClass);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHull);

        Class = shipClass;
        CargoCapacity = cargoCapacity;
        Firepower = firepower;
        MaxHull = maxHull;
        Hull = maxHull;
        Speed = speed;
    }

    public string Class { get; }
    public int CargoCapacity { get; }
    public int Firepower { get; }
    public int MaxHull { get; }
    public double Speed { get; }

    public int Hull { get; private set; }
    public bool IsDestroyed => Hull <= 0;

    /// <summary>Encaisse des dégâts. Retourne les dégâts NON absorbés, qui débordent sur le vaisseau suivant.</summary>
    public int TakeDamage(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        if (degats >= Hull)
        {
            var debordement = degats - Hull;
            Hull = 0;
            return debordement;
        }

        Hull -= degats;
        return 0;
    }

    public void Repair() => Hull = MaxHull;
}

public static class ShipFactory
{
    public static Ship Cargo() => new("Cargo", 500, 5, 40, 6.0);
    public static Ship Chasseur() => new("Chasseur", 0, 60, 25, 12.0);
    public static Ship Croiseur() => new("Croiseur", 120, 220, 180, 4.5);
    public static Ship Ravitailleur() => new("Ravitailleur", 1200, 0, 60, 3.5);
}

public sealed class Planet
{
    public const int MaxShieldLevel = 10;

    public Planet(PlanetCode code, string name, PlanetType type, FactionCode owner,
                  Coordinates position, long population)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(population);

        Code = code;
        Name = name;
        Type = type;
        Position = position;
        Owner = owner;
        Population = population;
    }

    // Identité et caractéristiques immuables
    public PlanetCode Code { get; }
    public string Name { get; }
    public PlanetType Type { get; }
    public Coordinates Position { get; }

    // État, modifiable uniquement par méthode métier
    public FactionCode Owner { get; private set; }
    public long Population { get; private set; }
    public int ShieldLevel { get; private set; }
    public int DefenseBatteries { get; private set; }

    public int DefenseRating => ShieldLevel * 50 + DefenseBatteries * 120;
    public bool IsColonized => Population > 0;
    public long FoodDemand => (Population + 999) / 1000;      // arrondi supérieur sans double

    // --- EXPLOITER ---

    public void Grow(long habitants)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(habitants);
        Population += habitants;
    }

    /// <summary>Applique une famine (−5 %). Retourne les habitants perdus, 0 si déjà vide.</summary>
    public long ApplyFamine()
    {
        if (Population == 0) return 0;                        // cas normal, pas une exception

        var perdus = Math.Min(Math.Max(1, Population / 20), Population);
        Population -= perdus;
        return perdus;
    }

    // --- DÉFENDRE ---

    /// <summary>Renforce le bouclier. Retourne false s'il est déjà au maximum.</summary>
    public bool UpgradeShield()
    {
        if (ShieldLevel >= MaxShieldLevel) return false;
        ShieldLevel++;
        return true;
    }

    public void BuildDefenseBattery() => DefenseBatteries++;

    /// <summary>Encaisse une manche d'assaut. Retourne la défense restante ; 0 = la planète tombe.</summary>
    public int AbsorbAssault(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        if (degats >= DefenseRating)
        {
            ShieldLevel = 0;
            DefenseBatteries = 0;
            return 0;
        }

        // Les batteries tombent avant le bouclier
        var batteriesDetruites = Math.Min(DefenseBatteries, degats / 120);
        DefenseBatteries -= batteriesDetruites;

        var residuel = degats - batteriesDetruites * 120;
        ShieldLevel = Math.Max(0, ShieldLevel - residuel / 50);

        return DefenseRating;
    }

    // --- GUERROYER ---

    /// <exception cref="DomainException">Faction déjà propriétaire, ou défenses encore debout.</exception>
    public void Capture(FactionCode conquerant)
    {
        if (conquerant == Owner)
            throw new DomainException($"{conquerant} possède déjà {Code}.");

        if (DefenseRating > 0)
            throw new DomainException(
                $"{Code} conserve {DefenseRating} points de défense : capture impossible.");

        Owner = conquerant;
        Population -= Population / 10;                        // 10 % de pertes civiles
    }
}

public sealed class Fleet
{
    private readonly List<Ship> _ships;

    public Fleet(Guid id, string name, FactionCode owner, PlanetCode location, IEnumerable<Ship> ships)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ships);

        _ships = [.. ships];
        if (_ships.Count == 0)
            throw new ArgumentException("Une flotte ne peut pas être créée vide.", nameof(ships));

        Id = id;
        Name = name;
        Owner = owner;
        Location = location;
        Status = FleetStatus.EnOrbite;
    }

    public Guid Id { get; }
    public string Name { get; }
    public FactionCode Owner { get; }                         // une flotte ne change pas de faction

    public PlanetCode Location { get; private set; }
    public PlanetCode? Destination { get; private set; }
    public FleetStatus Status { get; private set; }

    public IReadOnlyList<Ship> Ships => _ships;

    // Grandeurs dérivées — boucles manuelles : appelées à chaque tick
    public int Firepower
    {
        get { var t = 0; foreach (var s in _ships) t += s.Firepower; return t; }
    }

    public int CargoCapacity
    {
        get { var t = 0; foreach (var s in _ships) t += s.CargoCapacity; return t; }
    }

    /// <summary>La flotte va à la vitesse de son vaisseau le plus lent.</summary>
    public double Speed
    {
        get
        {
            if (_ships.Count == 0) return 0;
            var min = double.MaxValue;
            foreach (var s in _ships) if (s.Speed < min) min = s.Speed;
            return min;
        }
    }

    public bool IsCombatReady
    {
        get
        {
            if (Status != FleetStatus.EnOrbite) return false;
            foreach (var s in _ships) if (s.Firepower > 0) return true;
            return false;
        }
    }

    // --- COMMERCER / se déplacer ---

    /// <summary>Amorce un saut. Retourne false si la flotte n'est pas en orbite.</summary>
    public bool BeginJump(PlanetCode destination)
    {
        if (Status != FleetStatus.EnOrbite || destination == Location) return false;

        Destination = destination;
        Status = FleetStatus.EnTransit;
        return true;
    }

    /// <exception cref="DomainException">Si la flotte n'était pas en transit.</exception>
    public void Arrive()
    {
        if (Status != FleetStatus.EnTransit)
            throw new DomainException($"La flotte {Id} n'est pas en transit.");

        Location = Destination!.Value;
        Destination = null;
        Status = FleetStatus.EnOrbite;
    }

    /// <summary>Rappel du joueur pendant le trajet : retour à l'origine.</summary>
    public void Recall()
    {
        if (Status != FleetStatus.EnTransit)
            throw new DomainException($"La flotte {Id} n'est pas en transit.");

        Destination = null;
        Status = FleetStatus.EnOrbite;
    }

    // --- GUERROYER ---

    public void EngageCombat()
    {
        if (Status != FleetStatus.EnOrbite)
            throw new DomainException($"La flotte {Id} ne peut pas engager le combat depuis {Status}.");

        Status = FleetStatus.AuCombat;
    }

    /// <summary>Applique les dégâts d'une manche. Les plus faibles coques tombent d'abord.</summary>
    public int AbsorbDamage(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        _ships.Sort(static (a, b) => a.Hull.CompareTo(b.Hull));

        var restant = degats;
        var detruits = 0;

        foreach (var ship in _ships)
        {
            if (restant == 0) break;
            restant = ship.TakeDamage(restant);
            if (ship.IsDestroyed) detruits++;
        }

        if (detruits > 0) _ships.RemoveAll(static s => s.IsDestroyed);
        if (_ships.Count == 0) Status = FleetStatus.Detruite;

        return detruits;
    }

    public void Disengage()
    {
        if (Status != FleetStatus.Detruite) Status = FleetStatus.EnOrbite;
    }

    /// <exception cref="DomainException">Factions différentes, ou flottes en des lieux différents.</exception>
    public void Merge(Fleet autre)
    {
        ArgumentNullException.ThrowIfNull(autre);

        if (autre.Owner != Owner)
            throw new DomainException($"Impossible de fusionner {autre.Owner} avec {Owner}.");

        if (autre.Location != Location
            || autre.Status != FleetStatus.EnOrbite || Status != FleetStatus.EnOrbite)
            throw new DomainException("Les deux flottes doivent être en orbite sur la même planète.");

        _ships.AddRange(autre._ships);
        autre._ships.Clear();
        autre.Status = FleetStatus.Detruite;
    }
}
