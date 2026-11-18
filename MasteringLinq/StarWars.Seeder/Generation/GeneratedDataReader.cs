using System.Collections;
using System.Data.Common;

namespace StarWars.Seeder.Generation;

/// <summary>
/// DbDataReader "à la volée" : produit les lignes une par une, sans jamais les matérialiser en mémoire.
/// SqlBulkCopy le consomme directement (streaming) => 0 DataTable, empreinte mémoire constante même pour 10 M de lignes.
/// </summary>
public sealed class GeneratedDataReader(TableSpec spec) : DbDataReader
{
    private readonly object?[] _buffer = new object?[spec.Columns.Length];
    private long _next;

    public long RowsProduced => _next;

    public override bool Read()
    {
        if (_next >= spec.RowCount) return false;
        spec.Fill(_next, _buffer);
        _next++;
        return true;
    }

    public override int FieldCount => spec.Columns.Length;
    public override string GetName(int ordinal) => spec.Columns[ordinal].Name;
    public override Type GetFieldType(int ordinal) => spec.Columns[ordinal].Type;
    public override string GetDataTypeName(int ordinal) => spec.Columns[ordinal].Type.Name;
    public override int GetOrdinal(string name) =>
        Array.FindIndex(spec.Columns, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public override object GetValue(int ordinal) => _buffer[ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => _buffer[ordinal] is null;

    public override int GetValues(object[] values)
    {
        for (var i = 0; i < _buffer.Length; i++) values[i] = _buffer[i] ?? DBNull.Value;
        return _buffer.Length;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool HasRows => spec.RowCount > 0;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;

    public override bool GetBoolean(int ordinal) => (bool)_buffer[ordinal]!;
    public override byte GetByte(int ordinal) => (byte)_buffer[ordinal]!;
    public override char GetChar(int ordinal) => (char)_buffer[ordinal]!;
    public override DateTime GetDateTime(int ordinal) => (DateTime)_buffer[ordinal]!;
    public override decimal GetDecimal(int ordinal) => (decimal)_buffer[ordinal]!;
    public override double GetDouble(int ordinal) => (double)_buffer[ordinal]!;
    public override float GetFloat(int ordinal) => (float)_buffer[ordinal]!;
    public override Guid GetGuid(int ordinal) => (Guid)_buffer[ordinal]!;
    public override short GetInt16(int ordinal) => (short)_buffer[ordinal]!;
    public override int GetInt32(int ordinal) => (int)_buffer[ordinal]!;
    public override long GetInt64(int ordinal) => (long)_buffer[ordinal]!;
    public override string GetString(int ordinal) => (string)_buffer[ordinal]!;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
