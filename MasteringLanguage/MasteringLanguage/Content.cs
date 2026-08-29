using System;
using System.Collections.Generic;
using System.Text;

namespace Hyperlane.Core;

public enum Resource { Minerai, Cristal, Vivres, Carburant, Credits }
public enum PlanetType { Aride, Oceanique, Volcanique, Forestiere, CiteMonde, Glaciaire }
public enum FactionCode { CSL, KSY, VHR, CLB, Neutre }
public enum FleetStatus { EnOrbite, EnTransit, AuCombat, Detruite }

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

public sealed record Ship(string Class, int CargoCapacity, int Firepower, int Hull, double Speed);

public sealed record Planet(
	PlanetCode Code, string Name, PlanetType Type, FactionCode Owner,
	Coordinates Position, long Population, int ShieldLevel, int DefenseBatteries);

public sealed record Fleet(
	Guid Id, string Name, FactionCode Owner, PlanetCode Location,
	FleetStatus Status, IReadOnlyList<Ship> Ships);

public sealed record ResourceStock(Resource Resource, long Quantity);

public static class ShipClasses
{
	public static readonly Ship Cargo = new("Cargo", 500, 5, 40, 6.0);
	public static readonly Ship Chasseur = new("Chasseur", 0, 60, 25, 12.0);
	public static readonly Ship Croiseur = new("Croiseur", 120, 220, 180, 4.5);
	public static readonly Ship Ravitailleur = new("Ravitailleur", 1200, 0, 60, 3.5);
}
