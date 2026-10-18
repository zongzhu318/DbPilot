using DbStudio.Models;

namespace DbStudio.Providers;
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
public static class DbProviderFactory
{
    public static IDbProvider Create(ConnectionProfile profile) => profile.Ssh.Enabled?new SshDbProvider(profile):CreateDirect(profile);
    internal static IDbProvider CreateDirect(ConnectionProfile profile)=>profile.Kind == DatabaseKind.TDengine
        ? new TDengineProvider(profile) : new RelationalProvider(profile);
}
