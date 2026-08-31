using MasteringLanguage;
using System;

var a = new PointA(1, 2);
// a.x                                        // ❌ n'existe pas
var b = new PointB(1, 2);
Console.WriteLine(b.X);

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

// Déclarations de types (DOIVENT être après les top-level statements)
public struct PointA(int x, int y);          // paramètres capturés, RIEN d'exposé
public record struct PointB(int X, int Y);