// bench/Hyperlane.Benchmarks/Day1/Module01Benchmarks.cs
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;



[MemoryDiagnoser]
public class ParamsAndCollectionBenchmarks
{
    private Fleet _flotte = null!;
    private ResourceStock[] _lotMinier = null!;
    private ResourceStock[] _lotSecours = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flotte = new Fleet(Guid.CreateVersion7(), "Convoi-1", FactionCode.CSL, new PlanetCode("KES-01"),
            [ShipFactory.Cargo(), ShipFactory.Cargo(), ShipFactory.Ravitailleur()]);

        _lotMinier = [new(Resource.Minerai, 800), new(Resource.Cristal, 120)];
        _lotSecours = [new(Resource.Vivres, 400)];
    }

    [BenchmarkCategory("Validation"), Benchmark(Baseline = true)]
    public bool FitsIn_Legacy() => CargoManifestLegacy.FitsIn(_flotte,
        new ResourceStock(Resource.Minerai, 300),
        new ResourceStock(Resource.Vivres, 150),
        new ResourceStock(Resource.Carburant, 80));

    [BenchmarkCategory("Validation"), Benchmark]
    public bool FitsIn_Span() => CargoManifest.FitsIn(_flotte,
        new ResourceStock(Resource.Minerai, 300),
        new ResourceStock(Resource.Vivres, 150),
        new ResourceStock(Resource.Carburant, 80));

    [BenchmarkCategory("Convoi"), Benchmark()]
    public ResourceStock[] BuildConvoy_Concat() =>
        CargoManifestLegacy.BuildConvoy(_lotMinier, _lotSecours, new(Resource.Carburant, 200));

    [BenchmarkCategory("Convoi"), Benchmark]
    public ResourceStock[] BuildConvoy_Spread() =>
        CargoManifest.BuildConvoy(_lotMinier, _lotSecours, new(Resource.Carburant, 200));
}

