using System.Runtime.CompilerServices;

namespace StarWars.Seeder.Generation;

/// <summary>
/// Générateur pseudo-aléatoire SANS ÉTAT : la valeur ne dépend que de (table, ligne, colonne).
/// => reproductible à l'identique entre deux exécutions, et thread-safe (les tables se chargent en parallèle).
/// </summary>
public readonly struct Rng(long row, ulong tableSeed)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong z)
    {
        // finalizer de SplitMix64
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Next(int salt) =>
        Mix(tableSeed + (ulong)row * 0x9E3779B97F4A7C15UL + (ulong)(salt + 1) * 0xD1B54A32D192ED03UL);

    /// <summary>Entier dans [0, max[.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Int(int salt, int max) => (int)(Next(salt) % (ulong)max);

    /// <summary>Entier dans [min, max] inclus.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Between(int salt, int min, int max) => min + (int)(Next(salt) % (ulong)(max - min + 1));

    /// <summary>Identifiant 1-based dans [1, count].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Id(int salt, long count) => (int)(Next(salt) % (ulong)count) + 1;

    /// <summary>Vrai avec la probabilité <paramref name="percent"/> %.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Chance(int salt, int percent) => Next(salt) % 100 < (ulong)percent;

    /// <summary>Décimal entre min et max, avec 2 décimales.</summary>
    public decimal Money(int salt, long min, long max) =>
        (min * 100L + (long)(Next(salt) % (ulong)((max - min) * 100L + 1))) / 100m;

    public T Pick<T>(int salt, T[] items) => items[Int(salt, items.Length)];

    /// <summary>Jour dans [from, from + days[ .</summary>
    public DateTime Date(int salt, DateTime from, int days) => from.AddDays(Int(salt, days));
}

/// <summary>Répartition d'enfants sur des parents : garantit un ordre croissant de la clé parente (chargement séquentiel d'un PK composite).</summary>
public static class Spread
{
    /// <summary>
    /// Pour la ligne <paramref name="i"/> (0-based) parmi <paramref name="total"/> lignes réparties sur <paramref name="parents"/> parents :
    /// retourne l'id parent (1-based, croissant avec i) et le rang <paramref name="k"/> de la ligne chez ce parent.
    /// </summary>
    public static int Parent(long i, long total, long parents, out int k)
    {
        long p = i * parents / total;                      // 0-based, monotone
        long start = (p * total + parents - 1) / parents;  // première ligne de ce parent
        k = (int)(i - start);
        return (int)p + 1;
    }

    /// <summary>
    /// Clé "autre côté" d'une association N-N : distincte pour deux rangs k différents d'un même parent
    /// (pas = nombre premier, donc injectif modulo <paramref name="others"/> tant que k &lt; others).
    /// </summary>
    public static int Other(int parent, int k, long others) =>
        (int)(((long)parent * 7919L + (long)k * 104729L) % others) + 1;
}
