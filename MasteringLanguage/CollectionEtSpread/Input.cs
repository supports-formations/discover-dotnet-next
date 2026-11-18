


public static class CargoManifestLegacy
{
    public static long TotalUnits(params ResourceStock[] stocks)
    {
        var total = 0L;
        foreach (var stock in stocks) total += stock.Quantity;
        return total;
    }

    public static bool FitsIn(Fleet fleet, params ResourceStock[] stocks)
    {
        var capacity = fleet.Ships.Sum(s => s.CargoCapacity);
        return TotalUnits(stocks) <= capacity;
    }

    public static ResourceStock[] BuildConvoy(
        IEnumerable<ResourceStock> lotMinier,
        IEnumerable<ResourceStock> lotSecours,
        ResourceStock complement)
    {
        return lotMinier.Concat(lotSecours).Concat(new[] { complement }).ToArray();
    }
}

public static class CargoManifest
{
    // (1) params ReadOnlySpan<T> au lieu de params T[]
    public static long TotalUnits(params ReadOnlySpan<ResourceStock> stocks)
    {
        var total = 0L;
        foreach (var stock in stocks) total += stock.Quantity;
        return total;
    }

    public static bool FitsIn(Fleet fleet, params ReadOnlySpan<ResourceStock> stocks)
        // (2) le domaine expose déjà la capacité : ni énumérateur boxé, ni délégué
        => TotalUnits(stocks) <= fleet.CargoCapacity;

    // (3) collection expression + spread au lieu de Concat().ToArray()
    public static ResourceStock[] BuildConvoy(
        ReadOnlySpan<ResourceStock> lotMinier,
        ReadOnlySpan<ResourceStock> lotSecours,
        ResourceStock complement)
        => [.. lotMinier, .. lotSecours, complement];
}

