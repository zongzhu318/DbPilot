using System.Text;
using System.Text.Json;
using DbStudio.Models;
using DbStudio.Providers;

namespace DbStudio.Services;
public static class ResultExport
{
    public static async Task WriteAsync(QueryResult result,Stream output,string format,IDbProvider? provider,DatabaseKind kind,DbObject? target,CancellationToken ct)
    {
        if(format=="sql"&&(provider is null||target is null||result.Columns.Distinct(StringComparer.Ordinal).Count()!=result.Columns.Count))throw new ArgumentException("SQL 导出需要目标表与无重复的字段名称，请为重复列设置别名。");
        await using var writer=new StreamWriter(output,new UTF8Encoding(format=="csv"),leaveOpen:true);
        if(format=="csv")await writer.WriteLineAsync(string.Join(",",result.Columns.Select(SqlTools.CsvCell)).AsMemory(),ct);
        if(format=="json")await writer.WriteAsync("[".AsMemory(),ct);
        for(int index=0;index<result.Rows.Count;index++){
            ct.ThrowIfCancellationRequested();var row=result.Rows[index];
            var text=format switch{
                "csv"=>string.Join(",",row.Select(v=>{if(v is null or DBNull)return "\\N";var value=v is byte[] b?Convert.ToBase64String(b):SqlTools.Display(v);return SqlTools.CsvCell(value.StartsWith('\\')?"\\"+value:value);})),
                "json"=>(index==0?"":",\n")+JsonSerializer.Serialize(result.Columns.Select((n,i)=>new{name=n,value=row[i]})),
                "sql"=>$"INSERT INTO {provider!.Qualified(target!)} ({string.Join(", ",result.Columns.Select(provider.Quote))}) VALUES ({string.Join(", ",row.Select(v=>DataValues.Literal(v,kind)))});"+(kind==DatabaseKind.SqlServer?"\nGO":""),
                _=>throw new ArgumentException("导出格式无效。")};
            await writer.WriteLineAsync(text.AsMemory(),ct);
        }
        if(format=="json")await writer.WriteAsync("]".AsMemory(),ct);await writer.FlushAsync(ct);
    }
}
