using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DbStudio.Models;
using DbStudio.Providers;

namespace DbStudio.Services;
public static class CsvData
{
    public static async IAsyncEnumerable<string[]> ReadAsync(TextReader reader,[EnumeratorCancellation] CancellationToken ct)
    {
        var fields=new List<string>();var field=new StringBuilder();bool quoted=false,closed=false,started=false;int lineNo=0;string? line;
        while((line=await reader.ReadLineAsync(ct)) is not null){
            lineNo++;for(int i=0;i<line.Length;i++){
                char c=line[i];
                if(quoted){if(c=='"'){if(i+1<line.Length&&line[i+1]=='"'){field.Append('"');i++;}else{quoted=false;closed=true;}}else field.Append(c);}
                else if(c==','){fields.Add(field.ToString());field.Clear();closed=false;started=false;}
                else if(c=='"'&&!started&&!closed){quoted=true;started=true;}
                else{if(closed||c=='"')throw new FormatException($"CSV 第 {lineNo} 行引号格式无效。");field.Append(c);started=true;}
                if(field.Length>16*1024*1024||fields.Count>10000)throw new FormatException("CSV 单字段或列数超出限制。");
            }
            if(quoted){field.Append('\n');continue;}
            fields.Add(field.ToString());yield return fields.ToArray();fields.Clear();field.Clear();closed=false;started=false;
        }
        if(quoted)throw new FormatException("CSV 引号未闭合。");
    }
    public static async IAsyncEnumerable<object?[]> MapAsync(TextReader reader,bool header,IReadOnlyList<int> mapping,IReadOnlyList<ColumnInfo> columns,DatabaseKind kind,[EnumeratorCancellation] CancellationToken ct)
    {
        int line=0;await foreach(var row in ReadAsync(reader,ct)){
            line++;if(header&&line==1)continue;
            if(mapping.Any(i=>i<0||i>=row.Length))throw new FormatException($"CSV 记录 {line} 字段数量不匹配。");
            var values=new object?[mapping.Count];for(int i=0;i<values.Length;i++){var text=row[mapping[i]];values[i]=DataValues.ForColumn(text=="\\N"?null:text.StartsWith("\\\\",StringComparison.Ordinal)?text[1..]:text,columns[i],kind);}yield return values;
        }
    }
}
public static class DataTransfer
{
    public static async IAsyncEnumerable<QueryResult> TableBatches(IDbProvider provider,ConnectionProfile profile,DbObject table,[EnumeratorCancellation] CancellationToken ct)
    {
        if(profile.Kind!=DatabaseKind.TDengine){await foreach(var result in provider.StreamAsync("SELECT * FROM "+provider.Qualified(table),500,ct))yield return result;}
        else for(int page=0;;page++){
            var result=await provider.QueryAsync(provider.PageSql(table,"",page,500),500,ct);yield return result;if(result.Rows.Count<500)break;
        }
    }
    public static async Task<long> ExportAsync(IDbProvider provider,ConnectionProfile profile,DbObject table,Stream output,string format,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        if(format is not ("csv" or "json" or "sql"))throw new ArgumentException("不支持的导出格式");
        var metadata=format=="sql"?await provider.ColumnsAsync(table,ct):[];
        await using var writer=new StreamWriter(output,new UTF8Encoding(format=="csv"),leaveOpen:true);bool first=true;long count=0;
        bool identity=format=="sql"&&profile.Kind==DatabaseKind.SqlServer&&metadata.Any(c=>c.IsIdentity);
        if(identity)await writer.WriteLineAsync(($"SET IDENTITY_INSERT {provider.Qualified(table)} ON;\nGO").AsMemory(),ct);
        if(format=="json")await writer.WriteAsync("[".AsMemory(),ct);
        await foreach(var batch in TableBatches(provider,profile,table,ct)){
            var indices=Enumerable.Range(0,batch.Columns.Count).Where(i=>format!="sql"||metadata.Any(c=>c.Name==batch.Columns[i]&&(!c.IsGenerated||c.IsIdentity))).ToArray();
            if(format=="sql"&&indices.Length==0&&batch.Rows.Count>0)throw new InvalidOperationException("没有可导出的可写字段。");
            if(first&&format=="csv")await writer.WriteLineAsync(string.Join(",",batch.Columns.Select(SqlTools.CsvCell)).AsMemory(),ct);
            foreach(var row in batch.Rows){ct.ThrowIfCancellationRequested();
                string line=format switch{
                    "csv"=>string.Join(",",row.Select(v=>{if(v is null or DBNull)return "\\N";var text=v is byte[] b?Convert.ToBase64String(b):SqlTools.Display(v);return SqlTools.CsvCell(text.StartsWith('\\')?"\\"+text:text);})),
                    "json"=>(count==0?"":",\n")+JsonSerializer.Serialize(batch.Columns.Select((n,i)=>new KeyValuePair<string,object?>(n,row[i])).ToDictionary(p=>p.Key,p=>p.Value)),
                    "sql"=>$"INSERT INTO {provider.Qualified(table)} ({string.Join(", ",indices.Select(i=>provider.Quote(batch.Columns[i])))}) VALUES ({string.Join(", ",indices.Select(i=>DataValues.Literal(row[i],profile.Kind)))});"+(profile.Kind==DatabaseKind.SqlServer?"\nGO":""),
                    _=>throw new ArgumentException("不支持的导出格式")};
                await writer.WriteLineAsync(line.AsMemory(),ct);count++;
            }
            first=false;progress?.Report(new(count,"导出中"));
        }
        if(format=="json")await writer.WriteAsync("]".AsMemory(),ct);
        if(identity)await writer.WriteLineAsync(($"SET IDENTITY_INSERT {provider.Qualified(table)} OFF;\nGO").AsMemory(),ct);
        await writer.FlushAsync(ct);return count;
    }
    public static async Task<long> MigrateAsync(IDbProvider source,ConnectionProfile sourceProfile,DbObject sourceTable,IDbProvider target,ConnectionProfile targetProfile,DbObject targetTable,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        var sourceColumns=await source.ColumnsAsync(sourceTable,ct);var targetColumns=await target.ColumnsAsync(targetTable,ct);
        var names=targetColumns.Where(c=>!c.IsGenerated && !c.IsTag && sourceColumns.Any(s=>s.Name==c.Name)).Select(c=>c.Name).ToArray();
        if(names.Length==0)throw new InvalidOperationException("源表与目标表没有可写的同名字段。");
        async IAsyncEnumerable<object?[]> Rows([EnumeratorCancellation] CancellationToken token){
            await foreach(var batch in TableBatches(source,sourceProfile,sourceTable,token)){
                var indices=names.Select(n=>batch.Columns.ToList().IndexOf(n)).ToArray();if(indices.Any(i=>i<0))throw new InvalidOperationException("源表结构已变化。");
                foreach(var row in batch.Rows)yield return indices.Select(i=>row[i]).ToArray();
            }
        }
        return await target.InsertRowsAsync(targetTable,names,Rows(ct),progress,ct);
    }
    public static async Task BackupAsync(IDbProvider provider,ConnectionProfile profile,IReadOnlyList<DbObject> tables,Stream output,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        await using(var writer=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true)){
            await writer.WriteLineAsync($"-- DbStudio logical backup · {profile.Kind} · {DateTimeOffset.UtcNow:O}");
            await writer.FlushAsync(ct);
        }
        var foreignKeys=await provider.ForeignKeysAsync(profile.Database,ct);
        if(profile.Kind is DatabaseKind.PostgreSQL or DatabaseKind.SqlServer){
            await using var schemas=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true);
            foreach(var schema in tables.Select(t=>t.Schema).Where(s=>s.Length>0).Distinct()){
                var sql=profile.Kind==DatabaseKind.PostgreSQL?$"CREATE SCHEMA IF NOT EXISTS {provider.Quote(schema)};":$"IF SCHEMA_ID({DataValues.String(schema,profile.Kind)}) IS NULL EXEC({DataValues.String("CREATE SCHEMA "+provider.Quote(schema),profile.Kind)});\nGO";
                await schemas.WriteLineAsync(sql.AsMemory(),ct);
            }
            await schemas.FlushAsync(ct);
        }
        foreach(var table in tables){
            var columns=await provider.ColumnsAsync(table,ct);
            await using(var writer=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true)){
                await writer.WriteLineAsync(SchemaTools.CreateTable(provider,profile.Kind,table,columns));if(profile.Kind==DatabaseKind.SqlServer)await writer.WriteLineAsync("GO");
                await writer.FlushAsync(ct);
            }
            await ExportAsync(provider,profile,table,output,"sql",progress,ct);
            if(profile.Kind==DatabaseKind.PostgreSQL){
                await using var sequences=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true);
                foreach(var identity in columns.Where(c=>c.IsIdentity))await sequences.WriteLineAsync(($"SELECT setval(pg_get_serial_sequence({DataValues.String(provider.Qualified(table),profile.Kind)}, {DataValues.String(identity.Name,profile.Kind)}), COALESCE(MAX({provider.Quote(identity.Name)}), 1), MAX({provider.Quote(identity.Name)}) IS NOT NULL) FROM {provider.Qualified(table)};").AsMemory(),ct);
                await sequences.FlushAsync(ct);
            }
        }
        await using var tail=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true);
        foreach(var group in foreignKeys.Where(f=>tables.Any(t=>t.Name==f.Table&&(t.Schema==f.Schema||profile.Kind==DatabaseKind.MySQL&&t.Database==f.Schema))).GroupBy(f=>(f.Schema,f.Table,f.Name))){
            await tail.WriteLineAsync(SchemaTools.ForeignKeySql(provider,new(profile.Database,group.Key.Schema,group.Key.Table,"表"),group.ToArray()));if(profile.Kind==DatabaseKind.SqlServer)await tail.WriteLineAsync("GO");
        }
        await tail.FlushAsync(ct);
    }
}
