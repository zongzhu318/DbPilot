using System.Data.Common;
using System.Globalization;
using DbStudio.Models;
using MySqlConnector;
using Npgsql;
using Microsoft.Data.SqlClient;
using DbStudio.Services;

namespace DbStudio.Providers;
public sealed partial class RelationalProvider(ConnectionProfile profile) : IDbProvider, IEditableDbProvider
{
    private DbConnection? _connection;
    public static void ClearPool(ConnectionProfile profile)
    {
        if(profile.Ssh.Enabled)return;
        using var connection=new RelationalProvider(profile).CreateConnection();
        switch(connection){case MySqlConnection mysql:MySqlConnection.ClearPool(mysql);break;case NpgsqlConnection pg:NpgsqlConnection.ClearPool(pg);break;case SqlConnection sql:SqlConnection.ClearPool(sql);break;}
    }
    private DbConnection CreateConnection() => profile.Kind switch
        {
            DatabaseKind.MySQL => new MySqlConnection(new MySqlConnectionStringBuilder {
                Server = profile.Host, Port = (uint)profile.Port, Database = profile.Database,
                UserID = profile.Username, Password = profile.Password, ConnectionTimeout = (uint)profile.Timeout,
                MinimumPoolSize=(uint)profile.MinimumPoolSize,MaximumPoolSize=(uint)profile.MaximumPoolSize,Pooling=profile.UsePooling,
                AllowPublicKeyRetrieval = profile.AllowPublicKeyRetrieval,
                SslMode = profile.Ssl ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled }.ConnectionString),
            DatabaseKind.PostgreSQL => new NpgsqlConnection(new NpgsqlConnectionStringBuilder {
                Host = profile.Host, Port = profile.Port, Database = string.IsNullOrWhiteSpace(profile.Database) ? "postgres" : profile.Database,
                Username = profile.Username, Password = profile.Password, Timeout = profile.Timeout,
                MinPoolSize=profile.MinimumPoolSize,MaxPoolSize=profile.MaximumPoolSize,Pooling=profile.UsePooling,
                SslMode = profile.Ssl ? Npgsql.SslMode.VerifyFull : Npgsql.SslMode.Disable }.ConnectionString),
            _ => new SqlConnection(new SqlConnectionStringBuilder {
                DataSource = $"{profile.Host},{profile.Port}", InitialCatalog = profile.Database,
                UserID = profile.IntegratedSecurity ? "" : profile.Username, Password = profile.IntegratedSecurity ? "" : profile.Password,
                IntegratedSecurity = profile.IntegratedSecurity, ConnectTimeout = profile.Timeout,
                MinPoolSize=profile.MinimumPoolSize,MaxPoolSize=profile.MaximumPoolSize,Pooling=profile.UsePooling,
                Encrypt = profile.Ssl, TrustServerCertificate = false }.ConnectionString)
        };
    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (_connection is not null) return _connection;
        var connection=CreateConnection();
        try { await connection.OpenAsync(ct); _connection = connection; return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task TestAsync(CancellationToken ct) => await OpenAsync(ct);
    public string Quote(string name) => profile.Kind switch
    {
        DatabaseKind.MySQL => "`" + name.Replace("`", "``") + "`",
        DatabaseKind.SqlServer => "[" + name.Replace("]", "]]") + "]",
        _ => "\"" + name.Replace("\"", "\"\"") + "\""
    };
    public string Qualified(DbObject table) => profile.Kind == DatabaseKind.MySQL
        ? $"{Quote(table.Database)}.{Quote(table.Name)}" : string.IsNullOrEmpty(table.Schema) ? Quote(table.Name) : $"{Quote(table.Schema)}.{Quote(table.Name)}";
    public string PageSql(DbObject table, string filter, int page, int size)
    {
        if (page < 0 || size is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(size));
        var target=profile.Kind==DatabaseKind.MySQL&&profile.Database==table.Database?Quote(table.Name):Qualified(table);
        var sql = $"SELECT * FROM {target}" + (string.IsNullOrWhiteSpace(filter) ? "" : " WHERE " + filter);
        return profile.Kind switch {
            DatabaseKind.MySQL => $"{sql} LIMIT {size} OFFSET {(long)page * size};",
            DatabaseKind.PostgreSQL => $"{sql} OFFSET {(long)page * size} ROWS FETCH NEXT {size} ROWS ONLY;",
            _ => $"{sql} ORDER BY (SELECT NULL) OFFSET {(long)page * size} ROWS FETCH NEXT {size} ROWS ONLY;" };
    }
    public async Task<QueryResult> QueryAsync(string sql, int limit, CancellationToken ct)
    {
        var conn = await OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray();
            var rows = new List<object?[]>(); bool truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= limit) { truncated = true; break; }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return new(columns, rows, reader.RecordsAffected, truncated,types);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // SQL Server can leave a connection unusable after ATTENTION/cancel.
            // Drop it so the next operation opens a clean session.
            var stale = _connection;
            _connection = null;
            if (stale is not null) { try { await stale.DisposeAsync(); } catch { } }
            throw new OperationCanceledException(ct);
        }
    }
    private async Task<QueryResult> Metadata(string sql, CancellationToken ct, params (string, string)[] values)
    {
        var conn = await OpenAsync(ct); await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }
        await using var reader = await cmd.ExecuteReaderAsync(ct); var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct)) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return new([], rows, 0, false);
    }
    private static string Text(object? x) => x is null or DBNull ? "" : Convert.ToString(x, CultureInfo.InvariantCulture) ?? "";
    public async Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)
    {
        var sql = profile.Kind switch { DatabaseKind.MySQL => "SHOW DATABASES", DatabaseKind.PostgreSQL => "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname", _ => "SELECT name FROM sys.databases WHERE state=0 ORDER BY name" };
        return (await Metadata(sql, ct)).Rows.Select(r => Text(r[0])).ToArray();
    }
    public async Task<IReadOnlyList<DbObject>> ObjectsAsync(string database, CancellationToken ct)
    {
        var conn = await OpenAsync(ct);
        if (profile.Kind == DatabaseKind.SqlServer) await conn.ChangeDatabaseAsync(database, ct);
        var sql = profile.Kind switch {
            DatabaseKind.MySQL => "SELECT '',TABLE_NAME,CASE WHEN TABLE_TYPE='VIEW' THEN '视图' ELSE '表' END FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db UNION ALL SELECT '',ROUTINE_NAME,CASE WHEN ROUTINE_TYPE='PROCEDURE' THEN '存储过程' ELSE '函数' END FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@db UNION ALL SELECT '',TRIGGER_NAME,'触发器' FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@db ORDER BY 2",
            DatabaseKind.PostgreSQL => "SELECT table_schema,table_name,CASE WHEN table_type='VIEW' THEN '视图' ELSE '表' END,'' FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema') UNION ALL SELECT n.nspname,p.proname,CASE WHEN p.prokind='p' THEN '存储过程' ELSE '函数' END,pg_get_function_identity_arguments(p.oid) FROM pg_proc p JOIN pg_namespace n ON p.pronamespace=n.oid WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND p.prokind IN ('f','p') UNION ALL SELECT sequence_schema,sequence_name,'序列','' FROM information_schema.sequences ORDER BY 1,2",
            _ => "SELECT s.name,o.name,CASE o.type WHEN 'U' THEN N'表' WHEN 'V' THEN N'视图' WHEN 'P' THEN N'存储过程' ELSE N'函数' END FROM sys.objects o JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE o.type IN ('U','V','P','FN','TF','IF') AND o.is_ms_shipped=0 ORDER BY s.name,o.name" };
        return (await Metadata(sql, ct, ("@db", database))).Rows.Select(r => new DbObject(database, Text(r[0]), Text(r[1]), Text(r[2]),r.Length>3?Text(r[3]):"")).ToArray();
    }
    public async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table, CancellationToken ct)
    {
        var conn = await OpenAsync(ct);
        if (profile.Kind == DatabaseKind.SqlServer) await conn.ChangeDatabaseAsync(table.Database, ct);
        var sql = profile.Kind switch {
            DatabaseKind.MySQL => "SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_KEY,COLUMN_DEFAULT,COLUMN_COMMENT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION",
            DatabaseKind.PostgreSQL => "SELECT a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END,CASE WHEN EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND i.indisprimary AND a.attnum=ANY(i.indkey)) THEN 'PRI' ELSE '' END,pg_get_expr(d.adbin,d.adrelid),col_description(c.oid,a.attnum) FROM pg_attribute a JOIN pg_class c ON a.attrelid=c.oid JOIN pg_namespace n ON c.relnamespace=n.oid LEFT JOIN pg_attrdef d ON a.attrelid=d.adrelid AND a.attnum=d.adnum WHERE n.nspname=@schema AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum",
            _ => "SELECT c.name,t.name+CASE WHEN t.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN '('+CASE WHEN c.max_length=-1 THEN 'max' ELSE CONVERT(varchar(10),CASE WHEN t.name IN ('nvarchar','nchar') THEN c.max_length/2 ELSE c.max_length END) END+')' WHEN t.name IN ('decimal','numeric') THEN '('+CONVERT(varchar(10),c.precision)+','+CONVERT(varchar(10),c.scale)+')' ELSE '' END,CASE WHEN c.is_nullable=1 THEN 'YES' ELSE 'NO' END,CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE i.object_id=o.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id) THEN 'PRI' ELSE '' END,dc.definition,CAST(ep.value AS nvarchar(4000)) FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id JOIN sys.schemas s ON o.schema_id=s.schema_id JOIN sys.types t ON c.user_type_id=t.user_type_id LEFT JOIN sys.default_constraints dc ON c.default_object_id=dc.object_id LEFT JOIN sys.extended_properties ep ON ep.major_id=o.object_id AND ep.minor_id=c.column_id AND ep.name='MS_Description' WHERE s.name=@schema AND o.name=@table ORDER BY c.column_id" };
        var rows=await Metadata(sql, ct, ("@db", table.Database), ("@schema", table.Schema), ("@table", table.Name));
        var generatedSql=profile.Kind switch{
            DatabaseKind.MySQL=>"SELECT COLUMN_NAME,CASE WHEN EXTRA LIKE '%auto_increment%' THEN '1' ELSE '0' END,GENERATION_EXPRESSION,CASE WHEN EXTRA LIKE '%STORED GENERATED%' THEN '1' ELSE '0' END FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table AND (EXTRA LIKE '%auto_increment%' OR EXTRA LIKE '%VIRTUAL GENERATED%' OR EXTRA LIKE '%STORED GENERATED%')",
            DatabaseKind.PostgreSQL=>"SELECT a.attname,CASE WHEN a.attidentity<>'' OR pg_get_expr(d.adbin,d.adrelid) LIKE 'nextval(%' THEN '1' ELSE '0' END,CASE WHEN a.attgenerated<>'' THEN pg_get_expr(d.adbin,d.adrelid) ELSE '' END,CASE WHEN a.attgenerated='s' THEN '1' ELSE '0' END FROM pg_attribute a JOIN pg_class c ON a.attrelid=c.oid JOIN pg_namespace n ON c.relnamespace=n.oid LEFT JOIN pg_attrdef d ON a.attrelid=d.adrelid AND a.attnum=d.adnum WHERE n.nspname=@schema AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped AND (a.attidentity<>'' OR a.attgenerated<>'' OR pg_get_expr(d.adbin,d.adrelid) LIKE 'nextval(%')",
            _=>"SELECT c.name,CASE WHEN c.is_identity=1 THEN '1' ELSE '0' END,cc.definition,CASE WHEN cc.is_persisted=1 THEN '1' ELSE '0' END FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id JOIN sys.schemas s ON o.schema_id=s.schema_id JOIN sys.types t ON c.user_type_id=t.user_type_id LEFT JOIN sys.computed_columns cc ON c.object_id=cc.object_id AND c.column_id=cc.column_id WHERE s.name=@schema AND o.name=@table AND (c.is_identity=1 OR c.is_computed=1 OR t.name IN ('timestamp','rowversion'))"};
        var generated=(await Metadata(generatedSql,ct,("@db",table.Database),("@schema",table.Schema),("@table",table.Name))).Rows.ToDictionary(r=>Text(r[0]),StringComparer.Ordinal);
        return rows.Rows.Select(r => {generated.TryGetValue(Text(r[0]),out var g);return new ColumnInfo(Text(r[0]),Text(r[1]),Text(r[2]) == "YES",Text(r[3]) == "PRI",Text(r[4]),Text(r[5]),IsGenerated:g is not null,HasDefault:r[4] is not null and not DBNull,IsIdentity:g is {Length:>1}&&Text(g[1])=="1",GenerationExpression:g is {Length:>2}?Text(g[2]):"",Stored:g is {Length:>3}&&Text(g[3])=="1");}).ToArray();
    }
    public async Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table, CancellationToken ct)
    {
        var sql = profile.Kind switch {
            DatabaseKind.MySQL => "SELECT INDEX_NAME,COLUMN_NAME,CASE WHEN INDEX_NAME='PRIMARY' THEN '主键' WHEN NON_UNIQUE=0 THEN '唯一' ELSE '普通' END FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table ORDER BY INDEX_NAME,SEQ_IN_INDEX",
            DatabaseKind.PostgreSQL => "SELECT i.indexname,i.indexdef,CASE WHEN EXISTS(SELECT 1 FROM pg_constraint con JOIN pg_class ic ON con.conindid=ic.oid JOIN pg_namespace ns ON ic.relnamespace=ns.oid WHERE ic.relname=i.indexname AND ns.nspname=i.schemaname AND con.contype='p') THEN '主键' ELSE '索引定义' END FROM pg_indexes i WHERE i.schemaname=@schema AND i.tablename=@table",
            _ => "SELECT i.name,c.name,CASE WHEN i.is_primary_key=1 THEN '主键' WHEN i.is_unique=1 THEN '唯一' ELSE '普通' END FROM sys.indexes i JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON t.schema_id=s.schema_id WHERE t.name=@table AND s.name=@schema ORDER BY i.name,ic.key_ordinal" };
        return (await Metadata(sql,ct,("@db",table.Database),("@schema",table.Schema),("@table",table.Name))).Rows.Select(r => new IndexInfo(Text(r[0]),Text(r[1]),Text(r[2]))).ToArray();
    }
    public async ValueTask DisposeAsync() { if (_connection is not null) await _connection.DisposeAsync(); }
    public async Task<int> ApplyChangesAsync(DbObject table,IReadOnlyList<RowChange> changes,CancellationToken ct)
    {
        var columns=await ColumnsAsync(table,ct);
        var commands=EditCommandBuilder.Build(this,profile.Kind,table,columns,changes);
        if(profile.Kind==DatabaseKind.MySQL){
            var engine=await Metadata("SELECT e.TRANSACTIONS FROM information_schema.TABLES t JOIN information_schema.ENGINES e ON t.ENGINE=e.ENGINE WHERE t.TABLE_SCHEMA=@db AND t.TABLE_NAME=@table",ct,("@db",table.Database),("@table",table.Name));
            if(engine.Rows.Count!=1 || Text(engine.Rows[0][0])!="YES")throw new InvalidOperationException("该 MySQL 表引擎不支持事务，禁止批量在线编辑。");
        }
        return await RelationalBatchWriter.ApplyAsync(await OpenAsync(ct),commands,ct);
    }
}
