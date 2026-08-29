using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace MasteringLanguage
{
	// Struct normale
	public struct NormalStruct
	{
		public int Value1;
		public int Value2;
		public int Value3;
		public int Value4;

		public int Sum() => Value1 + Value2 + Value3 + Value4;
	}

	// Ref struct - stack only
	public ref struct RefStructVersion
	{
		public int Value1;
		public int Value2;
		public int Value3;
		public int Value4;

		public int Sum() => Value1 + Value2 + Value3 + Value4;
	}

	[MemoryDiagnoser]  // Affiche les allocations mémoire
	[SimpleJob(warmupCount: 3, iterationCount: 5)]
	public class StructBenchmarks
	{
		private const int Iterations = 10000;

		[Benchmark(Baseline = true)]
		public int NormalStruct_Processing()
		{
			int total = 0;
			for (int i = 0; i < Iterations; i++)
			{
				var data = new NormalStruct 
				{ 
					Value1 = i, 
					Value2 = i * 2, 
					Value3 = i * 3, 
					Value4 = i * 4 
				};
				total += data.Sum();
			}
			return total;
		}

		[Benchmark]
		public int RefStruct_Processing()
		{
			int total = 0;
			for (int i = 0; i < Iterations; i++)
			{
				var data = new RefStructVersion 
				{ 
					Value1 = i, 
					Value2 = i * 2, 
					Value3 = i * 3, 
					Value4 = i * 4 
				};
				total += data.Sum();
			}
			return total;
		}

		[Benchmark]
		public int NormalStruct_WithBoxing()
		{
			int total = 0;
			for (int i = 0; i < Iterations; i++)
			{
				var data = new NormalStruct { Value1 = i };
				// Boxing = allocation heap
				object boxed = data;
				total += ((NormalStruct)boxed).Value1;
			}
			return total;
		}

		// Benchmark avec Span<T> - nécessite ref struct
		[Benchmark]
		public int WithSpan_RefStruct()
		{
			Span<int> numbers = stackalloc int[100];

			for (int i = 0; i < 100; i++)
				numbers[i] = i;

			return ProcessWithRefStruct(numbers);
		}

		[Benchmark]
		public int WithArray_NormalStruct()
		{
			int[] numbers = new int[100]; // Allocation heap

			for (int i = 0; i < 100; i++)
				numbers[i] = i;

			return ProcessWithArray(numbers);
		}

		private int ProcessWithRefStruct(Span<int> numbers)
		{
			int sum = 0;
			foreach (var n in numbers)
				sum += n;
			return sum;
		}

		private int ProcessWithArray(int[] numbers)
		{
			int sum = 0;
			foreach (var n in numbers)
				sum += n;
			return sum;
		}
	}

	public class BenchmarkExecutor
	{
		public static void RunBenchmarks()
		{
			var summary = BenchmarkRunner.Run<StructBenchmarks>();
			Console.WriteLine("\n✅ Benchmarks terminés !");
			Console.WriteLine("Les résultats montrent :");
			Console.WriteLine("- Temps d'exécution (Mean)");
			Console.WriteLine("- Allocations mémoire (Allocated)");
			Console.WriteLine("- Gen0/Gen1/Gen2 collections");
		}
	}
}
