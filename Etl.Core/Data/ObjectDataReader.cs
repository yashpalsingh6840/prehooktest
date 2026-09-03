using System.Collections;
using System.Data;
using System.Data.Common;

namespace Etl.Core.Data;

/// <summary>
/// Streams an IAsyncEnumerable&lt;T&gt; to SqlBulkCopy without ever materializing a DataTable.
/// Deriving from DbDataReader (rather than plain IDataReader) is what lets SqlBulkCopy pull
/// via WriteToServerAsync(DbDataReader, ...), which calls ReadAsync() on this type -- a genuinely
/// async, non-buffering path from the CSV source all the way to the TDS wire.
/// </summary>
public sealed class ObjectDataReader<T> : DbDataReader
{
    private readonly IAsyncEnumerator<T> _source;
    private readonly IReadOnlyList<EntityColumn> _columns;
    private readonly Dictionary<string, int> _ordinals;
    private T _current = default!;
    private bool _closed;

    public long RowsRead { get; private set; }

    public ObjectDataReader(IAsyncEnumerable<T> source, IReadOnlyList<EntityColumn> columns, CancellationToken ct)
    {
        _source = source.GetAsyncEnumerator(ct);
        _columns = columns;
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) _ordinals[columns[i].PropertyName] = i;
    }

    public override int FieldCount => _columns.Count;
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;
    public override int VisibleFieldCount => _columns.Count;

    public override string GetName(int ordinal) => _columns[ordinal].PropertyName;
    public override int GetOrdinal(string name) => _ordinals[name];
    public override Type GetFieldType(int ordinal) => _columns[ordinal].ProviderType;
    public override string GetDataTypeName(int ordinal) => _columns[ordinal].ProviderType.Name;

    public override object GetValue(int ordinal) => _columns[ordinal].GetValue(_current!) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _columns.Count);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool NextResult() => false;

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _source.MoveNextAsync()) return false;
        _current = _source.Current;
        RowsRead++;
        return true;
    }

    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable");
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("AllowDBNull", typeof(bool));

        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            table.Rows.Add(column.PropertyName, i, column.MaxLength ?? -1, column.ProviderType, true);
        }

        return table;
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _closed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        _closed = true;
        await base.DisposeAsync();
    }
}
