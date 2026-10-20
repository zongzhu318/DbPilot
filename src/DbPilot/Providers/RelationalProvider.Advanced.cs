using System.Data;
using System.Runtime.CompilerServices;
using DbPilot.Models;
using DbPilot.Services;
using Npgsql;
using NpgsqlTypes;

namespace DbPilot.Providers;

public sealed partial class RelationalProvider
{
    public async Task<IReadOnlyList<QueryResult>> ExplainAsync(string sql,CancellationToken ct)
    {
        if(profile.Kind!=DatabaseKind.SqlServer)return await QueryMultipleAsync("EXPLAIN "+sql,500,ct);
        await QueryAsync("SET SHOWPLAN_TEXT ON",1,ct);
        try{return await QueryMultipleAsync(sql,500,ct);}
        finally{await QueryAsync("SET SHOWPLAN_TEXT OFF",1,CancellationToken.None);}
    }
    public async Task<IReadOnlyList<QueryResult>> QueryMultipleAsync(string sql,int limit,CancellationToken ct)
    {
        var connection=await OpenAsync(ct);await using var command=connection.CreateCommand();command.CommandText=sql;command.CommandTimeout=0;
        await using var reader=await command.ExecuteReaderAsync(ct);var results=new List<QueryResult>();
        do{
            var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();var types=Enumerable.Range(0,reader.FieldCount).Select(reader.GetFieldType).ToArray();
            var rows=new List<object?[]>();bool truncated=false;
            while(await reader.ReadAsync(ct)){
                if(rows.Count>=limit){truncated=true;continue;}
                var values=new object?[reader.FieldCount];for(int i=0;i<values.Length;i++)values[i]=await reader.IsDBNullAsync(i,ct)?null:reader.GetValue(i);rows.Add(values);
            }
            results.Add(new(columns,rows,reader.RecordsAffected,truncated,types));
            if(results.Count>100)throw new InvalidOperationException("一次最多保留 100 个结果集，请拆分脚本。");
        }while(await reader.NextResultAsync(ct));
        return results;
    }
    public async IAsyncEnumerable<QueryResult> StreamAsync(string sql,int batchSize,[EnumeratorCancellation] CancellationToken ct)
    {
        if(batchSize is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(batchSize));
        var connection=await OpenAsync(ct);await using var command=connection.CreateCommand();command.CommandText=sql;command.CommandTimeout=0;
        await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);
        var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();var types=Enumerable.Range(0,reader.FieldCount).Select(reader.GetFieldType).ToArray();var rows=new List<object?[]>();
        while(await reader.ReadAsync(ct)){
            var values=new object?[reader.FieldCount];for(int i=0;i<values.Length;i++)values[i]=await reader.IsDBNullAsync(i,ct)?null:reader.GetValue(i);rows.Add(values);
            if(rows.Count==batchSize){yield return new(columns,rows,0,false,types);rows=[];}
        }
        if(rows.Count>0 || reader.FieldCount>0)yield return new(columns,rows,0,false,types);
    }
    public async Task<long> InsertRowsAsync(DbObject table,IReadOnlyList<string> columns,IAsyncEnumerable<object?[]> rows,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        if(table.Kind!="表")throw new InvalidOperationException("批量写入仅支持普通表。");
        var metadata=await ColumnsAsync(table,ct);var known=metadata.ToDictionary(c=>c.Name,StringComparer.Ordinal);
        if(columns.Count==0 || columns.Distinct(StringComparer.Ordinal).Count()!=columns.Count || columns.Any(n=>!known.TryGetValue(n,out var col)||col.IsGenerated))throw new InvalidOperationException("映射字段重复、无效或包含生成字段。");
        if(profile.Kind==DatabaseKind.MySQL){
            var engine=await Metadata("SELECT e.TRANSACTIONS FROM information_schema.TABLES t JOIN information_schema.ENGINES e ON t.ENGINE=e.ENGINE WHERE t.TABLE_SCHEMA=@db AND t.TABLE_NAME=@table",ct,("@db",table.Database),("@table",table.Name));
            if(engine.Rows.Count!=1 || Text(engine.Rows[0][0])!="YES")throw new InvalidOperationException("批量写入要求支持事务的表引擎。");
        }
        var connection=await OpenAsync(ct);var tx=await connection.BeginTransactionAsync(ct);bool committing=false;long count=0;
        try{
            await using var command=connection.CreateCommand();command.Transaction=tx;command.CommandTimeout=30;
            command.CommandText=$"INSERT INTO {Qualified(table)} ({string.Join(", ",columns.Select(Quote))}) VALUES ({string.Join(", ",columns.Select((_,i)=>"@v"+i))});";
            for(int i=0;i<columns.Count;i++){var p=command.CreateParameter();p.ParameterName="@v"+i;command.Parameters.Add(p);}
            await foreach(var row in rows.WithCancellation(ct)){
                if(row.Length!=columns.Count)throw new InvalidOperationException("导入行与字段映射数量不一致。");
                for(int i=0;i<row.Length;i++){
                    var value=DataValues.ForColumn(row[i],known[columns[i]],profile.Kind);var p=command.Parameters[i];p.Value=value??DBNull.Value;
                    if(p is NpgsqlParameter np){np.ResetDbType();if(value is string)np.NpgsqlDbType=NpgsqlDbType.Unknown;}
                }
                await command.ExecuteNonQueryAsync(ct);count++;if(count%200==0)progress?.Report(new(count,"写入中（事务未提交）"));
            }
            ct.ThrowIfCancellationRequested();committing=true;await tx.CommitAsync(CancellationToken.None);progress?.Report(new(count,"写入已提交"));return count;
        }catch(Exception ex){bool failed=false;try{await tx.RollbackAsync(CancellationToken.None);}catch{failed=true;}
            if(committing||failed)throw new EditCommitUncertainException("批量写入结果未知，请核对目标表，禁止直接重复导入。",ex);throw;
        }finally{try{await tx.DisposeAsync();}catch{}}
    }
    public async Task<string> DefinitionAsync(DbObject obj,CancellationToken ct)
    {
        if(profile.Kind==DatabaseKind.MySQL){
            var kind=obj.Kind switch{"视图"=>"VIEW","存储过程"=>"PROCEDURE","函数"=>"FUNCTION","触发器"=>"TRIGGER",_=>"TABLE"};
            var result=await QueryAsync($"SHOW CREATE {kind} {Qualified(obj)}",1,ct);
            int index=result.Columns.ToList().FindIndex(c=>c.StartsWith("Create ",StringComparison.OrdinalIgnoreCase)||c=="SQL Original Statement");
            if(result.Rows.Count>0 && index>=0)return Text(result.Rows[0][index]);
        }
        if(profile.Kind==DatabaseKind.SqlServer && obj.Kind!="表"){
            var result=await Metadata("SELECT OBJECT_DEFINITION(OBJECT_ID(@name))",ct,("@name",Qualified(obj)));return result.Rows.Count>0?Text(result.Rows[0][0]):"";
        }
        if(profile.Kind==DatabaseKind.PostgreSQL && obj.Kind is "函数" or "存储过程"){
            var result=await Metadata("SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON p.pronamespace=n.oid WHERE n.nspname=@schema AND p.proname=@name AND pg_get_function_identity_arguments(p.oid)=@args",ct,("@schema",obj.Schema),("@name",obj.Name),("@args",obj.Signature));
            return string.Join(";\n",result.Rows.Select(r=>Text(r[0])));
        }
        if(profile.Kind==DatabaseKind.PostgreSQL&&obj.Kind=="序列"){
            var result=await Metadata("SELECT data_type::text,start_value,min_value,max_value,increment_by,cycle,cache_size FROM pg_sequences WHERE schemaname=@schema AND sequencename=@name",ct,("@schema",obj.Schema),("@name",obj.Name));
            if(result.Rows.Count!=1)throw new InvalidOperationException("无法读取序列定义。");var r=result.Rows[0];
            return $"CREATE SEQUENCE {Qualified(obj)} AS {SchemaTools.ValidType(Text(r[0]))} START WITH {Text(r[1])} MINVALUE {Text(r[2])} MAXVALUE {Text(r[3])} INCREMENT BY {Text(r[4])} {(Text(r[5]).Equals("True",StringComparison.OrdinalIgnoreCase)?"CYCLE":"NO CYCLE")} CACHE {Text(r[6])};";
        }
        if(profile.Kind==DatabaseKind.PostgreSQL && obj.Kind=="视图"){
            var result=await Metadata("SELECT pg_get_viewdef(c.oid,true) FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid WHERE n.nspname=@schema AND c.relname=@name",ct,("@schema",obj.Schema),("@name",obj.Name));
            return $"CREATE VIEW {Qualified(obj)} AS\n"+(result.Rows.Count>0?Text(result.Rows[0][0]):"");
        }
        var columns=await ColumnsAsync(obj,ct);var fks=await ForeignKeysAsync(obj.Database,ct);
        return SchemaTools.CreateTable(this,profile.Kind,obj,columns)+"\n"+string.Join("\n",fks.Where(f=>f.Schema==obj.Schema&&f.Table==obj.Name).GroupBy(f=>f.Name).Select(g=>SchemaTools.ForeignKeySql(this,obj,g.ToArray())));
    }
    public async Task<IReadOnlyList<ForeignKeyInfo>> ForeignKeysAsync(string database,CancellationToken ct)
    {
        var connection=await OpenAsync(ct);if(profile.Kind==DatabaseKind.SqlServer)await connection.ChangeDatabaseAsync(database,ct);
        var sql=profile.Kind switch{
            DatabaseKind.MySQL=>"SELECT k.CONSTRAINT_NAME,k.TABLE_SCHEMA,k.TABLE_NAME,k.COLUMN_NAME,k.REFERENCED_TABLE_SCHEMA,k.REFERENCED_TABLE_NAME,k.REFERENCED_COLUMN_NAME,r.DELETE_RULE,r.UPDATE_RULE FROM information_schema.KEY_COLUMN_USAGE k JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA=k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME=k.CONSTRAINT_NAME AND r.TABLE_NAME=k.TABLE_NAME WHERE k.TABLE_SCHEMA=@db AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.CONSTRAINT_NAME,k.ORDINAL_POSITION",
            DatabaseKind.PostgreSQL=>"SELECT con.conname,ns.nspname,c.relname,a.attname,rns.nspname,rc.relname,ra.attname,CASE con.confdeltype WHEN 'a' THEN 'NO ACTION' WHEN 'r' THEN 'RESTRICT' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' END,CASE con.confupdtype WHEN 'a' THEN 'NO ACTION' WHEN 'r' THEN 'RESTRICT' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' END,con.condeferrable,con.condeferred FROM pg_constraint con JOIN pg_class c ON con.conrelid=c.oid JOIN pg_namespace ns ON c.relnamespace=ns.oid JOIN pg_class rc ON con.confrelid=rc.oid JOIN pg_namespace rns ON rc.relnamespace=rns.oid JOIN LATERAL unnest(con.conkey,con.confkey) WITH ORDINALITY AS k(att,refatt,ord) ON true JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.att JOIN pg_attribute ra ON ra.attrelid=rc.oid AND ra.attnum=k.refatt WHERE con.contype='f' ORDER BY con.conname,k.ord",
            _=>"SELECT fk.name,s.name,t.name,c.name,rs.name,rt.name,rc.name,REPLACE(fk.delete_referential_action_desc,'_',' '),REPLACE(fk.update_referential_action_desc,'_',' ') FROM sys.foreign_keys fk JOIN sys.foreign_key_columns f ON fk.object_id=f.constraint_object_id JOIN sys.tables t ON f.parent_object_id=t.object_id JOIN sys.schemas s ON t.schema_id=s.schema_id JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=f.parent_column_id JOIN sys.tables rt ON f.referenced_object_id=rt.object_id JOIN sys.schemas rs ON rt.schema_id=rs.schema_id JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=f.referenced_column_id ORDER BY fk.name,f.constraint_column_id"};
        return (await Metadata(sql,ct,("@db",database))).Rows.Select(r=>new ForeignKeyInfo(Text(r[0]),Text(r[1]),Text(r[2]),Text(r[3]),Text(r[4]),Text(r[5]),Text(r[6]),r.Length>7?Text(r[7]):"NO ACTION",r.Length>8?Text(r[8]):"NO ACTION",r.Length>9&&r[9] is true,r.Length>10&&r[10] is true)).ToArray();
    }
}
