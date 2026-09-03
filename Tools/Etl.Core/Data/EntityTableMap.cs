using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Etl.Core.Data;

/// <summary>One column, as SqlBulkCopy needs to see it: name, provider-side CLR type, and a
/// compiled accessor that already applies any registered EF value converter.</summary>
public sealed record EntityColumn(string PropertyName, string ColumnName, Type ProviderType, int? MaxLength, Func<object, object?> GetValue);

/// <summary>
/// Destination table metadata derived from EF Core's own model -- not attributes, not magic
/// strings -- so a renamed column, a HasMaxLength(), or a registered value converter are all
/// honored automatically by the bulk-copy path. Built once per (DbContext type, entity type)
/// and cached; the getters are compiled expression trees, not per-row reflection.
/// </summary>
public sealed class EntityTableMap
{
    private static readonly ConcurrentDictionary<(Type ContextType, Type EntityType), EntityTableMap> Cache = new();

    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<EntityColumn> Columns { get; init; }

    public string QuotedName => $"[{Schema}].[{Table}]";

    public static EntityTableMap For(DbContext context, Type entityType) =>
        Cache.GetOrAdd((context.GetType(), entityType), _ => Build(context, entityType));

    private static EntityTableMap Build(DbContext context, Type entityType)
    {
        var entityTypeMeta = context.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException(
                $"'{entityType.Name}' is not part of the EF model for {context.GetType().Name}.");

        var tableName = entityTypeMeta.GetTableName()
            ?? throw new InvalidOperationException($"'{entityType.Name}' has no mapped table name.");
        var schema = entityTypeMeta.GetSchema() ?? "dbo";

        var columns = entityTypeMeta.GetProperties()
            .Select(BuildColumn)
            .ToArray();

        return new EntityTableMap { Schema = schema, Table = tableName, Columns = columns };
    }

    private static EntityColumn BuildColumn(IProperty property)
    {
        var clrProperty = property.PropertyInfo
            ?? throw new InvalidOperationException($"Property '{property.Name}' has no backing CLR property.");

        var columnName = property.GetColumnName()
            ?? throw new InvalidOperationException($"Property '{property.Name}' has no mapped column name.");

        var converter = property.GetValueConverter();
        var providerType = converter?.ProviderClrType ?? clrProperty.PropertyType;

        var rawGetter = BuildRawGetter(property.DeclaringType.ClrType, clrProperty);
        Func<object, object?> getValue = converter is null
            ? rawGetter
            : entity => converter.ConvertToProvider(rawGetter(entity));

        return new EntityColumn(clrProperty.Name, columnName, providerType, property.GetMaxLength(), getValue);
    }

    /// <summary>Compiled `entity => (object)((TEntity)entity).Property` -- built once, not per row.</summary>
    private static Func<object, object?> BuildRawGetter(Type entityType, PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var typedInstance = Expression.Convert(instance, entityType);
        var propertyAccess = Expression.Property(typedInstance, property);
        var boxed = Expression.Convert(propertyAccess, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, instance).Compile();
    }
}
