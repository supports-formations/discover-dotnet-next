```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12500H 2.50GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-NTRUNJ : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=5  WarmupCount=3  

```
| Method                          | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1    | Gen2    | Allocated | Alloc Ratio |
|-------------------------------- |-----------:|-----------:|----------:|------:|--------:|---------:|--------:|--------:|----------:|------------:|
| CsvParsing_String_10K           |   3.288 μs |  0.2174 μs | 0.0565 μs |  1.00 |    0.02 |   1.3580 |       - |       - |   12800 B |        1.00 |
| CsvParsing_Span_10K             |   2.661 μs |  0.0849 μs | 0.0220 μs |  0.81 |    0.01 |        - |       - |       - |         - |        0.00 |
| CsvParsing_String_100K          |  30.649 μs |  3.4333 μs | 0.8916 μs |  9.32 |    0.29 |  10.8643 |       - |       - |  102400 B |        8.00 |
| CsvParsing_Span_100K            |  25.672 μs |  1.4535 μs | 0.3775 μs |  7.81 |    0.16 |        - |       - |       - |         - |        0.00 |
| CsvParsing_String_1M            | 306.448 μs | 36.5749 μs | 9.4984 μs | 93.22 |    3.02 | 105.9570 |       - |       - | 1002400 B |       78.31 |
| CsvParsing_Span_1M              | 237.795 μs |  3.7525 μs | 0.5807 μs | 72.34 |    1.15 |        - |       - |       - |         - |        0.00 |
| ArrayProcessing_NewArray_10K    |   6.661 μs |  0.5142 μs | 0.1335 μs |  2.03 |    0.05 |   4.2343 |       - |       - |   40024 B |        3.13 |
| ArrayProcessing_Stackalloc_10K  |   5.275 μs |  0.1109 μs | 0.0288 μs |  1.60 |    0.03 |        - |       - |       - |         - |        0.00 |
| ArrayProcessing_NewArray_100K   | 121.437 μs |  1.6368 μs | 0.4251 μs | 36.94 |    0.59 |  56.6406 | 56.6406 | 56.6406 |  400043 B |       31.25 |
| ArrayProcessing_Stackalloc_100K |  56.878 μs |  0.8220 μs | 0.2135 μs | 17.30 |    0.28 |        - |       - |       - |         - |        0.00 |
| ArraySlicing_Copy_100K          |  43.201 μs |  2.5490 μs | 0.6620 μs | 13.14 |    0.28 |  42.7246 |       - |       - |  402400 B |       31.44 |
| ArraySlicing_Span_100K          |  24.103 μs |  0.2700 μs | 0.0701 μs |  7.33 |    0.12 |        - |       - |       - |         - |        0.00 |
| StringSplitting_Split_10K       |   7.548 μs |  0.3232 μs | 0.0839 μs |  2.30 |    0.04 |   5.0964 |  0.7248 |       - |   48032 B |        3.75 |
| StringSplitting_Span_10K        |   3.674 μs |  0.0920 μs | 0.0142 μs |  1.12 |    0.02 |        - |       - |       - |         - |        0.00 |
