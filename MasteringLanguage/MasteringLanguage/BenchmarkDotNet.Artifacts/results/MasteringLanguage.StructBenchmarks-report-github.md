```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12500H 2.50GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-NTRUNJ : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=5  WarmupCount=3  

```
| Method                  | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------------ |-------------:|-------------:|-----------:|------:|--------:|--------:|----------:|------------:|
| NormalStruct_Processing |  9,549.96 ns |   106.074 ns |  16.415 ns | 1.000 |    0.00 |       - |         - |          NA |
| RefStruct_Processing    | 13,442.98 ns |   539.234 ns | 140.037 ns | 1.408 |    0.01 |       - |         - |          NA |
| NormalStruct_WithBoxing | 21,955.60 ns | 1,416.066 ns | 219.138 ns | 2.299 |    0.02 | 33.9966 |  320000 B |          NA |
| WithSpan_RefStruct      |     64.38 ns |     0.828 ns |   0.215 ns | 0.007 |    0.00 |       - |         - |          NA |
| WithArray_NormalStruct  |     62.18 ns |     3.669 ns |   0.568 ns | 0.007 |    0.00 |       - |         - |          NA |
