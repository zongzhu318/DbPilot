using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DbPilot.Models;

namespace DbPilot.Providers;
// TDengine uses taosAdapter REST (default port 6041), independently of ADO.NET.
public sealed class TDengineProvider : IDbProvider
{
    private readonly ConnectionProfile _profile;
    private readonly HttpClient _http;
    public TDengineProvider(ConnectionProfile profile,HttpMessageHandler? handler=null)
    {
        _profile = profile;
        _http = (handler is null ? new HttpClient() : new HttpClient(handler)); _http.Timeout=Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Username}:{profile.Password}")));
    }
    public string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    public string Qualified(DbObject table) => $"{Quote(table.Database)}.{Quote(table.Name)}";
    public string PageSql(DbObject table, string filter, int page, int size)=>PageSql(table,"*",filter,page,size);
    private string PageSql(DbObject table,string select,string filter,int page,int size)
    {
        if (page < 0 || size is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(size));
        return $"SELECT {select} FROM {(_profile.Database==table.Database?Quote(table.Name):Qualified(table))}" + (string.IsNullOrWhiteSpace(filter) ? "" : " WHERE " + filter) + $" LIMIT {size} OFFSET {(long)page * size};";
    }
    public BrowseQuery BuildBrowse(DbObject table,IReadOnlyList<ColumnInfo> columns,string filter,int page,int size)
    {
        var previews=Services.LargeColumns.PreviewIndexes(columns,DatabaseKind.TDengine);
        if(previews.Count==0)return new(PageSql(table,filter,page,size),previews);
        var previewSet=previews.ToHashSet();
        var select=string.Join(", ",columns.Select((column,index)=>previewSet.Contains(index)?$"SUBSTR({Quote(column.Name)}, 1, {Services.LargeColumns.PreviewLength}) AS {Quote(column.Name)}":Quote(column.Name)));
        return new(PageSql(table,select,filter,page,size),previews);
    }
    public async Task TestAsync(CancellationToken ct) => await QueryAsync("SELECT SERVER_VERSION()", 1, ct);
    public async Task<QueryResult> QueryAsync(string sql, int limit, CancellationToken ct)
    {
        var builder = new UriBuilder(_profile.Ssl ? "https" : "http", _profile.Host, _profile.Port, "rest/sql" + (string.IsNullOrWhiteSpace(_profile.Database) ? "" : "/" + Uri.EscapeDataString(_profile.Database)));
        builder.Query = "tz=" + Uri.EscapeDataString(_profile.Timezone) + "&row_with_meta=true";
        using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri) { Content = new StringContent(sql, Encoding.UTF8, "text/plain") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        // Hard transport cap prevents an unbounded REST response from exhausting memory.
        using var buffer = new MemoryStream(); var bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0) { if (buffer.Length + count > 32 * 1024 * 1024) throw new InvalidOperationException("TDengine 响应超过 32 MB，请添加 LIMIT 或缩小时间范围。"); await buffer.WriteAsync(bytes.AsMemory(0,count),ct); }
        buffer.Position = 0; using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: ct); var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0) throw new InvalidOperationException(root.TryGetProperty("desc",out var desc) ? desc.GetString() : "TDengine 查询失败");
        var columns = root.TryGetProperty("column_meta", out var meta) ? meta.EnumerateArray().Select(x => x[0].GetString() ?? "").ToArray() : [];
        var rows = new List<object?[]>(); bool truncated = false;
        if (root.TryGetProperty("data", out var data)) foreach (var row in data.EnumerateArray()) {
            if (rows.Count == limit) { truncated = true; break; }
            var cells=row.ValueKind==JsonValueKind.Object ? columns.Select(name=>row.GetProperty(name)) : row.EnumerateArray().ToArray().AsEnumerable();
            rows.Add(cells.Select(x => x.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => (object?)x.GetString(), _ => x.ToString() }).ToArray());
        }
        return new(columns, rows, 0, truncated);
    }
    public async Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct) => (await QueryAsync("SHOW DATABASES",10000,ct)).Rows.Select(x => x[0]?.ToString() ?? "").ToArray();
    public async Task<IReadOnlyList<DbObject>> ObjectsAsync(string database, CancellationToken ct)
    {
        var tables = await QueryAsync($"SELECT table_name, stable_name FROM information_schema.ins_tables WHERE db_name = {Services.DataValues.String(database,DatabaseKind.TDengine)}",10000,ct);
        var stables = await QueryAsync($"SHOW {Quote(database)}.STABLES",10000,ct);
        int parentColumn=tables.Columns.ToList().FindIndex(c=>c.Equals("stable_name",StringComparison.OrdinalIgnoreCase));
        return stables.Rows.Select(x=>new DbObject(database,"",x[0]?.ToString() ?? "","超级表")).Concat(tables.Rows.Select(x=>{
            string parent=parentColumn>=0?x[parentColumn]?.ToString()??"":"";return new DbObject(database,"",x[0]?.ToString()??"",parent.Length>0?"子表":"普通表",Parent:parent);
        })).ToArray();
    }
    public async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table, CancellationToken ct) => (await QueryAsync($"DESCRIBE {Qualified(table)}",10000,ct)).Rows.Select(r=>{
        var type=r[1]?.ToString()??"";if(type is "BINARY" or "NCHAR" or "VARCHAR" or "VARBINARY")type+="("+r[2]+")";
        return new ColumnInfo(r[0]?.ToString()??"",type,true,false,"",r.Length>3?r[3]?.ToString()??"":"",r.Length>3&&r[3]?.ToString()=="TAG");}).ToArray();
    public async Task<string> DefinitionAsync(DbObject obj,CancellationToken ct)
    {
        var result=await QueryAsync($"SHOW CREATE {(obj.Kind=="超级表"?"STABLE":"TABLE")} {Qualified(obj)}",1,ct);
        if(result.Rows.Count==0||result.Rows[0].Length<2)throw new InvalidOperationException("服务器未返回对象定义。");
        return result.Rows[0][1]?.ToString()??"";
    }
    public async Task<long> InsertRowsAsync(DbObject table,IReadOnlyList<string> columns,IAsyncEnumerable<object?[]> rows,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        if(table.Kind is not ("普通表" or "子表"))throw new InvalidOperationException("TDengine 导入需选择普通表或子表，不能直接写入超级表。");
        var metadata=(await ColumnsAsync(table,ct)).ToDictionary(c=>c.Name,StringComparer.Ordinal);
        if(columns.Count==0||columns.Distinct().Count()!=columns.Count||columns.Any(n=>!metadata.TryGetValue(n,out var c)||c.IsTag))throw new InvalidOperationException("映射字段无效、重复或包含标签。");
        string head=$"INSERT INTO {Qualified(table)} ({string.Join(", ",columns.Select(Quote))}) VALUES ";
        var values=new List<string>();int bytes=0;long committed=0;bool sending=false;
        async Task Flush(){if(values.Count==0)return;ct.ThrowIfCancellationRequested();sending=true;await QueryAsync(head+string.Join(" ",values),1,ct);sending=false;committed+=values.Count;values.Clear();bytes=0;progress?.Report(new(committed,"TDengine 已写入（无跨批次事务）"));}
        try{
            await foreach(var row in rows.WithCancellation(ct)){
                if(row.Length!=columns.Count)throw new FormatException("导入行与字段映射数量不一致。");
                string tuple="("+string.Join(", ",row.Select((v,i)=>Services.DataValues.Literal(Services.DataValues.ForColumn(v,metadata[columns[i]],DatabaseKind.TDengine),DatabaseKind.TDengine)))+")";
                int size=Encoding.UTF8.GetByteCount(tuple);if(size>512*1024)throw new FormatException("单行数据超过 512 KB，请调整导入内容。");
                if(values.Count>=200||bytes+size>512*1024)await Flush();values.Add(tuple);bytes+=size;
            }
            await Flush();return committed;
        }catch(Exception ex){throw new InvalidOperationException($"TDengine 导入中断；已确认写入 {committed} 行。{(sending?"当前批次结果未知，请核对服务器。":"")}TDengine 不支持跨批次回滚，请核对数据后再继续。",ex);}
    }
    public Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct) => Task.FromResult<IReadOnlyList<IndexInfo>>([]);
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}
