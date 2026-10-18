using DbStudio.Models;
using DbStudio.Providers;

namespace DbStudio.Services;

public static class EditCommandBuilder
{
    public static IReadOnlyList<EditCommand> Build(IDbProvider provider,DatabaseKind kind,DbObject table,IReadOnlyList<ColumnInfo> columns,IReadOnlyList<RowChange> changes)
    {
        if(kind==DatabaseKind.TDengine || table.Kind!="表")throw new InvalidOperationException("仅支持关系型普通表的事务编辑。");
        var schema=columns.ToDictionary(c=>c.Name,StringComparer.Ordinal);
        var keys=columns.Where(c=>c.PrimaryKey).ToArray();if(keys.Length==0)throw new InvalidOperationException("无主键表不允许在线编辑。");
        var commands=new List<EditCommand>();
        foreach(var change in changes){
            if(change.Values.Keys.Any(n=>!schema.TryGetValue(n,out var column)||column.IsGenerated))throw new InvalidOperationException("变更包含未知字段或自动生成字段，请刷新表结构。");
            if(change.Kind!=RowChangeKind.Insert && (!schema.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(change.Original.Keys)||keys.Any(k=>change.Original[k.Name] is null or DBNull)))throw new InvalidOperationException("原始数据或主键信息不完整，请刷新后重试。");
            var parameters=new List<EditParameter>();
            string Param(object? value){var name="@p"+parameters.Count;parameters.Add(new(name,value is DBNull ? null : value));return name;}
            foreach(var (name,value) in change.Values)if(value is null or DBNull && !schema[name].Nullable)throw new InvalidOperationException($"字段 {name} 不允许 NULL。");
            string sql;
            if(change.Kind==RowChangeKind.Insert){
                var names=change.Values.Keys.ToArray();
                sql=names.Length==0 ? kind==DatabaseKind.MySQL ? $"INSERT INTO {provider.Qualified(table)} () VALUES ();" : $"INSERT INTO {provider.Qualified(table)} DEFAULT VALUES;" : $"INSERT INTO {provider.Qualified(table)} ({string.Join(", ",names.Select(provider.Quote))}) VALUES ({string.Join(", ",names.Select(n=>Param(change.Values[n])))});";
            }else{
                // Include the complete original row to reject stale edits/deletes atomically.
                string Predicate(ColumnInfo column){
                    var name=provider.Quote(column.Name);var value=change.Original[column.Name];if(value is null or DBNull)return name+" IS NULL";
                    var type=column.Type.ToLowerInvariant();
                    if(kind==DatabaseKind.MySQL && type=="json")return name+" = CAST("+Param(value)+" AS JSON)";
                    if(kind==DatabaseKind.PostgreSQL && type is "json" or "jsonb" or "xml")name+="::text";
                    if(kind==DatabaseKind.SqlServer && type is "text" or "ntext" or "xml")name=$"CONVERT(nvarchar(max), {name})";
                    return name+" = "+Param(value);
                }
                var set=change.Kind==RowChangeKind.Update ? string.Join(", ",change.Values.Select(p=>provider.Quote(p.Key)+" = "+Param(p.Value))) : "";
                if(change.Kind==RowChangeKind.Update && set.Length==0)continue;
                var where=string.Join(" AND ",columns.OrderByDescending(c=>c.PrimaryKey).Select(Predicate));
                sql=change.Kind==RowChangeKind.Delete ? $"DELETE FROM {provider.Qualified(table)} WHERE {where};" : $"UPDATE {provider.Qualified(table)} SET {set} WHERE {where};";
            }
            commands.Add(new(sql,parameters));
        }
        return commands;
    }
}
