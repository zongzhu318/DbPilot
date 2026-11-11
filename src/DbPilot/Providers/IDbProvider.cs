using DbPilot.Models;

namespace DbPilot.Providers;
public sealed record BrowseQuery(string Sql,IReadOnlyList<int> PreviewColumns);
public sealed record ObjectPage(IReadOnlyList<string> Names,bool HasMore);
public interface IDbProvider : IAsyncDisposable
{
    Task TestAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct);
    Task<IReadOnlyList<DbObject>> ObjectsAsync(string database, CancellationToken ct);
    Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table, CancellationToken ct);
    Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table, CancellationToken ct);
    Task<QueryResult> QueryAsync(string sql, int limit, CancellationToken ct);
    string Quote(string name);
    string Qualified(DbObject table);
    string PageSql(DbObject table, string filter, int page, int size);
    BrowseQuery BuildBrowse(DbObject table,IReadOnlyList<ColumnInfo> columns,string filter,int page,int size)=>new(PageSql(table,filter,page,size),[]);
    Task<object?[]> ReadValuesAsync(DbObject table,IReadOnlyList<ColumnInfo> columns,IReadOnlyList<ColumnInfo> keyColumns,IReadOnlyList<object?> keyValues,CancellationToken ct)=>throw new NotSupportedException("当前连接不支持按主键读取字段。");
    async Task<IReadOnlyList<QueryResult>> QueryMultipleAsync(string sql,int limit,CancellationToken ct)=>[await QueryAsync(sql,limit,ct)];
    Task<IReadOnlyList<QueryResult>> ExplainAsync(string sql,CancellationToken ct)=>QueryMultipleAsync("EXPLAIN "+sql,500,ct);
    IAsyncEnumerable<QueryResult> StreamAsync(string sql,int batchSize,CancellationToken ct)=>throw new NotSupportedException("该 Provider 不支持任意 SQL 流式读取，请使用整表分页导出。");
    Task<string> DefinitionAsync(DbObject obj,CancellationToken ct)=>throw new NotSupportedException("该对象定义暂不可读取。");
    Task<IReadOnlyList<ForeignKeyInfo>> ForeignKeysAsync(string database,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ForeignKeyInfo>>([]);
    Task<long> InsertRowsAsync(DbObject table,IReadOnlyList<string> columns,IAsyncEnumerable<object?[]> rows,IProgress<TransferProgress>? progress,CancellationToken ct)=>throw new NotSupportedException("该 Provider 不支持批量写入。");
}
public interface IEditableDbProvider
{
    Task<int> ApplyChangesAsync(DbObject table,IReadOnlyList<RowChange> changes,CancellationToken ct);
}
// Optional capability for metadata that can be paged on the server. stableName=null means ordinary tables.
public interface IPagedObjectProvider
{
    Task<IReadOnlyList<string>> StablesAsync(string database,CancellationToken ct);
    Task<ObjectPage> TablesPageAsync(string database,string? stableName,string? search,int offset,int pageSize,CancellationToken ct);
    Task<long> CountTablesAsync(string database,string? stableName,string? search,CancellationToken ct);
    Task<long> TableRankAsync(string database,string? stableName,string name,CancellationToken ct);
    Task<IReadOnlyList<DbObject>> ObjectsLiteAsync(string database,CancellationToken ct);
}
public static class DbProviderExtensions
{
    public static Task<IReadOnlyList<DbObject>> ObjectsLiteAsync(this IDbProvider provider,string database,CancellationToken ct)
        => provider is IPagedObjectProvider paged ? paged.ObjectsLiteAsync(database,ct) : provider.ObjectsAsync(database,ct);
}
// In-memory paging used when a wrapper hides a paging provider; keeps the same ordering as the server.
public static class ObjectPaging
{
    public static ObjectPage Slice(IEnumerable<DbObject> objects,string? stableName,string? search,int offset,int pageSize)
    {
        if(offset<0||pageSize is <1 or >500)throw new ArgumentOutOfRangeException(nameof(offset));
        var names=Filter(objects,stableName,search).Select(o=>o.Name).ToArray();
        return new(names.Skip(offset).Take(pageSize).ToArray(),names.Length>offset+pageSize);
    }
    public static long Count(IEnumerable<DbObject> objects,string? stableName,string? search)=>Filter(objects,stableName,search).Count();
    public static long Rank(IEnumerable<DbObject> objects,string? stableName,string name)
        => Filter(objects,stableName,null).Count(o=>string.CompareOrdinal(o.Name,name)<0);
    private static IEnumerable<DbObject> Filter(IEnumerable<DbObject> objects,string? stableName,string? search)
    {
        var scoped=stableName is null
            ? objects.Where(o=>o.Kind=="普通表")
            : objects.Where(o=>o.Kind=="子表"&&o.Parent==stableName);
        if(string.IsNullOrEmpty(search))return scoped.OrderBy(o=>o.Name,StringComparer.Ordinal);
        return scoped.Where(o=>o.Name.Contains(search,StringComparison.OrdinalIgnoreCase)).OrderBy(o=>o.Name,StringComparer.Ordinal);
    }
}
public static class DbProviderFactory
{
    public static IDbProvider Create(ConnectionProfile profile) => profile.Ssh.Enabled?new SshDbProvider(profile):CreateDirect(profile);
    internal static IDbProvider CreateDirect(ConnectionProfile profile)=>profile.Kind == DatabaseKind.TDengine
        ? new TDengineProvider(profile) : new RelationalProvider(profile);
}
