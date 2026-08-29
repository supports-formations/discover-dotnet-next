using MasteringLanguage;
using System;

// Pour tester la correction du ref struct
// TestStruct.Test();

// ===== BENCHMARKS SIMPLES (petites structures) =====
// BenchmarkExecutor.RunBenchmarks();

// ===== BENCHMARKS GROS VOLUMES (la vraie différence !) =====
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║  🔥 BENCHMARK: GROS VOLUMES - Span vs Arrays/Strings   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝\n");
Console.WriteLine("⚠️  IMPORTANT: Compilez en mode RELEASE !");
Console.WriteLine("   (Build > Configuration Manager > Release)\n");
Console.WriteLine("📊 Ce benchmark va comparer :");
Console.WriteLine("   - 10K, 100K, 1M caractères (CSV parsing)");
Console.WriteLine("   - new[] vs stackalloc (allocations)");
Console.WriteLine("   - Array.Copy vs Span.Slice (zero-copy)");
Console.WriteLine("   - String.Split vs Span slicing\n");
Console.WriteLine("⏱️  Durée estimée : ~2-3 minutes\n");

BigDataBenchmarkRunner.RunBigDataBenchmarks();