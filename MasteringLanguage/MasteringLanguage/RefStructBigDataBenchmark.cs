using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace MasteringLanguage
{
	// Scénario réaliste : Parser de données CSV
	public ref struct CsvParserRefStruct
	{
		private ReadOnlySpan<char> data;

		public CsvParserRefStruct(ReadOnlySpan<char> csvData)
		{
			data = csvData;
		}

		public int CountFields()
		{
			int count = 0;
			foreach (var c in data)
			{
				if (c == ',') count++;
			}
			return count;
		}

		public ReadOnlySpan<char> GetSlice(int start, int length)
		{
			return data.Slice(start, Math.Min(length, data.Length - start));
		}
	}

	// Version avec struct normale et string
	public struct CsvParserNormalStruct
	{
		private string data;

		public CsvParserNormalStruct(string csvData)
		{
			data = csvData;
		}

		public int CountFields()
		{
			int count = 0;
			foreach (var c in data)
			{
				if (c == ',') count++;
			}
			return count;
		}

		public string GetSlice(int start, int length)
		{
			return data.Substring(start, Math.Min(length, data.Length - start));
		}
	}

	[MemoryDiagnoser]
	[SimpleJob(warmupCount: 3, iterationCount: 5)]
	public class BigDataBenchmarks
	{
		private string largeString_10K;
		private string largeString_100K;
		private string largeString_1M;

		private int[] largeArray_10K;
		private int[] largeArray_100K;
		private int[] largeArray_1M;

		[GlobalSetup]
		public void Setup()
		{
			// Créer de grosses chaînes CSV
			largeString_10K = GenerateCsvString(10_000);
			largeString_100K = GenerateCsvString(100_000);
			largeString_1M = GenerateCsvString(1_000_000);

			// Créer de gros tableaux
			largeArray_10K = GenerateArray(10_000);
			largeArray_100K = GenerateArray(100_000);
			largeArray_1M = GenerateArray(1_000_000);
		}

		private string GenerateCsvString(int size)
		{
			var chars = new char[size];
			for (int i = 0; i < size; i++)
			{
				chars[i] = (i % 10 == 0) ? ',' : (char)('A' + (i % 26));
			}
			return new string(chars);
		}

		private int[] GenerateArray(int size)
		{
			var array = new int[size];
			for (int i = 0; i < size; i++)
			{
				array[i] = i;
			}
			return array;
		}

		// ============================================
		// 1. CSV PARSING - 10K caractères
		// ============================================

		[Benchmark(Baseline = true)]
		public int CsvParsing_String_10K()
		{
			var parser = new CsvParserNormalStruct(largeString_10K);
			int total = parser.CountFields();

			// Créer des substrings (allocations !)
			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 50, 50);
				total += slice.Length;
			}
			return total;
		}

		[Benchmark]
		public int CsvParsing_Span_10K()
		{
			ReadOnlySpan<char> spanData = largeString_10K.AsSpan();
			var parser = new CsvParserRefStruct(spanData);
			int total = parser.CountFields();

			// Créer des slices (ZERO allocation !)
			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 50, 50);
				total += slice.Length;
			}
			return total;
		}

		// ============================================
		// 2. CSV PARSING - 100K caractères
		// ============================================

		[Benchmark]
		public int CsvParsing_String_100K()
		{
			var parser = new CsvParserNormalStruct(largeString_100K);
			int total = parser.CountFields();

			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 500, 500);
				total += slice.Length;
			}
			return total;
		}

		[Benchmark]
		public int CsvParsing_Span_100K()
		{
			ReadOnlySpan<char> spanData = largeString_100K.AsSpan();
			var parser = new CsvParserRefStruct(spanData);
			int total = parser.CountFields();

			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 500, 500);
				total += slice.Length;
			}
			return total;
		}

		// ============================================
		// 3. CSV PARSING - 1M caractères
		// ============================================

		[Benchmark]
		public int CsvParsing_String_1M()
		{
			var parser = new CsvParserNormalStruct(largeString_1M);
			int total = parser.CountFields();

			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 5000, 5000);
				total += slice.Length;
			}
			return total;
		}

		[Benchmark]
		public int CsvParsing_Span_1M()
		{
			ReadOnlySpan<char> spanData = largeString_1M.AsSpan();
			var parser = new CsvParserRefStruct(spanData);
			int total = parser.CountFields();

			for (int i = 0; i < 100; i++)
			{
				var slice = parser.GetSlice(i * 5000, 5000);
				total += slice.Length;
			}
			return total;
		}

		// ============================================
		// 4. ARRAY PROCESSING - 10K éléments
		// ============================================

		[Benchmark]
		public int ArrayProcessing_NewArray_10K()
		{
			// Allocation d'un nouveau tableau à CHAQUE appel
			int[] data = new int[10_000];
			for (int i = 0; i < data.Length; i++)
			{
				data[i] = i * 2;
			}

			int sum = 0;
			foreach (var n in data)
				sum += n;
			return sum;
		}

		[Benchmark]
		public int ArrayProcessing_Stackalloc_10K()
		{
			// Stack allocation - ZERO heap !
			Span<int> data = stackalloc int[10_000];
			for (int i = 0; i < data.Length; i++)
			{
				data[i] = i * 2;
			}

			int sum = 0;
			foreach (var n in data)
				sum += n;
			return sum;
		}

		// ============================================
		// 5. ARRAY PROCESSING - 100K éléments
		// ============================================

		[Benchmark]
		public long ArrayProcessing_NewArray_100K()
		{
			// Énorme allocation heap
			int[] data = new int[100_000];
			for (int i = 0; i < data.Length; i++)
			{
				data[i] = i * 2;
			}

			long sum = 0;
			foreach (var n in data)
				sum += n;
			return sum;
		}

		[Benchmark]
		public long ArrayProcessing_Stackalloc_100K()
		{
			// Stack allocation (attention à la taille de stack !)
			Span<int> data = stackalloc int[100_000];
			for (int i = 0; i < data.Length; i++)
			{
				data[i] = i * 2;
			}

			long sum = 0;
			foreach (var n in data)
				sum += n;
			return sum;
		}

		// ============================================
		// 6. ARRAY SLICING - Copie vs Référence
		// ============================================

		[Benchmark]
		public int ArraySlicing_Copy_100K()
		{
			// Créer des COPIES de sous-tableaux (allocations !)
			int total = 0;
			for (int i = 0; i < 100; i++)
			{
				int[] slice = new int[1000];
				Array.Copy(largeArray_100K, i * 1000, slice, 0, 1000);

				foreach (var n in slice)
					total += n;
			}
			return total;
		}

		[Benchmark]
		public int ArraySlicing_Span_100K()
		{
			// Créer des RÉFÉRENCES (zero copy, zero allocation !)
			int total = 0;
			Span<int> fullData = largeArray_100K.AsSpan();

			for (int i = 0; i < 100; i++)
			{
				Span<int> slice = fullData.Slice(i * 1000, 1000);

				foreach (var n in slice)
					total += n;
			}
			return total;
		}

		// ============================================
		// 7. STRING SPLITTING - Split vs Span
		// ============================================

		[Benchmark]
		public int StringSplitting_Split_10K()
		{
			// String.Split() = allocations massives !
			string[] parts = largeString_10K.Split(',');
			int total = 0;
			foreach (var part in parts)
			{
				total += part.Length;
			}
			return total;
		}

		[Benchmark]
		public int StringSplitting_Span_10K()
		{
			// Manual split avec Span = zero allocation
			ReadOnlySpan<char> data = largeString_10K.AsSpan();
			int total = 0;
			int lastIndex = 0;

			for (int i = 0; i < data.Length; i++)
			{
				if (data[i] == ',')
				{
					var slice = data.Slice(lastIndex, i - lastIndex);
					total += slice.Length;
					lastIndex = i + 1;
				}
			}
			return total;
		}
	}

	public class BigDataBenchmarkRunner
	{
		public static void RunBigDataBenchmarks()
		{
			Console.WriteLine("🚀 Lancement des benchmarks GROS VOLUMES...\n");
			Console.WriteLine("Comparaisons :");
			Console.WriteLine("- String vs Span (CSV parsing)");
			Console.WriteLine("- new[] vs stackalloc");
			Console.WriteLine("- Array.Copy vs Span.Slice");
			Console.WriteLine("- String.Split vs Span slicing\n");

			var summary = BenchmarkRunner.Run<BigDataBenchmarks>();

			Console.WriteLine("\n✅ Benchmarks gros volumes terminés !");
			Console.WriteLine("\n📊 Regardez les colonnes :");
			Console.WriteLine("- Allocated : Mémoire allouée (B = Bytes, KB = KiloBytes)");
			Console.WriteLine("- Gen0/Gen1/Gen2 : Collections du Garbage Collector");
			Console.WriteLine("- Plus c'est bas, mieux c'est !");
		}
	}
}
