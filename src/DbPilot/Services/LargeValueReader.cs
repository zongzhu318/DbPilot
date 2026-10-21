using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.Services;

// Reads full values for browse previews on demand; never writes preview text back to the database.
public sealed class LargeValueReader : IAsyncDisposable
{
    private readonly IDbProvider _provider;
    private readonly DbObject _table;
    private readonly IReadOnlyList<ColumnInfo> _columns;
    private readonly IReadOnlyList<ColumnInfo> _keys;
    private readonly IReadOnlyList<int> _resultIndexes;
    private readonly IReadOnlyList<int> _keyPositions;
    public IReadOnlyList<int> ResultIndexes=>_resultIndexes;
    private LargeValueReader(IDbProvider provider,DbObject table,IReadOnlyList<ColumnInfo> columns,IReadOnlyList<ColumnInfo> keys,IReadOnlyList<int> resultIndexes,IReadOnlyList<int> keyPositions)
    {_provider=provider;_table=table;_columns=columns;_keys=keys;_resultIndexes=resultIndexes;_keyPositions=keyPositions;}
    public static LargeValueReader? Create(QueryTab? tab,QueryResult result,out string? reason)=>Create(tab,result,null,out reason);
    public static LargeValueReader? Create(QueryTab? tab,QueryResult result,Func<ConnectionProfile,IDbProvider>? providerFactory,out string? reason)
    {
        reason=null;
        if(tab?.Table is not {} table){reason="当前结果不是表浏览页，请重新打开表后再试。";return null;}
        if(tab.Connection is not {} profile){reason="表浏览页的连接已失效。";return null;}
        if(tab.TableColumns is not {} tableColumns){reason="缺少表结构信息，请重新打开表。";return null;}
        if(tab.PreviewColumns is not {Count:>0} previews){reason="当前结果没有大字段预览列。";return null;}
        var byName=tableColumns.GroupBy(c=>c.Name,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var columns=new ColumnInfo[result.Columns.Count];
        for(int i=0;i<columns.Length;i++){
            if(!byName.TryGetValue(result.Columns[i],out var column)){reason="查询列与表结构不一致，请重新打开表。";return null;}
            columns[i]=column;
        }
        var keys=Enumerable.Range(0,columns.Length).Where(i=>columns[i].PrimaryKey).ToArray();
        if(keys.Length==0){reason="该对象没有主键，无法按行读取完整内容；请在 SQL 中显式查询大字段。";return null;}
        var previewIndexes=previews.Where(i=>i>=0&&i<columns.Length).Distinct().OrderBy(i=>i).ToArray();
        if(previewIndexes.Length==0){reason="当前结果没有可加载的预览字段。";return null;}
        var provider=(providerFactory??DbProviderFactory.Create)(profile);
        return new(provider,table,previewIndexes.Select(i=>columns[i]).ToArray(),keys.Select(i=>columns[i]).ToArray(),previewIndexes,keys);
    }
    public int PositionOf(int resultIndex)
    {
        for(int i=0;i<_resultIndexes.Count;i++)if(_resultIndexes[i]==resultIndex)return i;
        return -1;
    }
    // Values shorter than the projection were not truncated and need no extra round trip.
    public bool IsComplete(IReadOnlyList<object?> rowValues)
    {
        foreach(var index in _resultIndexes){
            if(index<0||index>=rowValues.Count)return false;
            var value=rowValues[index];
            if(value is null or DBNull)continue;
            if(value is string text){if(text.Length>=LargeColumns.PreviewLength)return false;continue;}
            if(value is byte[] bytes){if(bytes.Length>=LargeColumns.PreviewLength)return false;continue;}
            return false;
        }
        return true;
    }
    public Task<object?[]> ReadAsync(IReadOnlyList<object?> rowValues,CancellationToken ct)
    {
        var keys=_keyPositions.Select(i=>i<rowValues.Count&&rowValues[i] is not (null or DBNull)?rowValues[i]:null).ToArray();
        if(keys.Any(v=>v is null))throw new InvalidOperationException("主键值缺失，无法读取完整内容。");
        return _provider.ReadValuesAsync(_table,_columns,_keys,keys,ct);
    }
    public async Task<QueryResult> ReadFullResultAsync(QueryResult result,CancellationToken ct)
    {
        var rows=result.Rows.Select(r=>(object?[])r.Clone()).ToArray();
        for(int index=0;index<rows.Length;index++){
            ct.ThrowIfCancellationRequested();
            if(IsComplete(rows[index]))continue;
            var values=await ReadAsync(rows[index],ct);
            for(int i=0;i<values.Length;i++)rows[index][_resultIndexes[i]]=values[i];
        }
        return result with{Rows=rows,PreviewColumns=[]};
    }
    public ValueTask DisposeAsync()=>_provider.DisposeAsync();
}
