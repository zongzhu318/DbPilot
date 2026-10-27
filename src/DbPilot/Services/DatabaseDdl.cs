using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.Services;
public static class DatabaseDdl
{
    public static string Name(string? name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Any(char.IsControl))throw new ArgumentException("名称不能为空或包含控制字符。");return name.Trim();
    }
    public static string CreateDatabase(IDbProvider provider,DatabaseKind kind,string name)=>kind switch{
        DatabaseKind.MySQL=>$"CREATE DATABASE {provider.Quote(Name(name))} CHARACTER SET utf8mb4;",
        DatabaseKind.PostgreSQL=>$"CREATE DATABASE {provider.Quote(Name(name))} ENCODING 'UTF8' TEMPLATE template0;",
        DatabaseKind.TDengine=>$"CREATE DATABASE {provider.Quote(Name(name))} PRECISION 'ms';",
        _=>$"CREATE DATABASE {provider.Quote(Name(name))};"};
    public static string RenameTable(IDbProvider provider,DatabaseKind kind,DbObject table,string name)
    {
        name=Name(name);if(name==table.Name)throw new ArgumentException("新名称与原名称相同。");
        return kind switch{
            DatabaseKind.MySQL=>$"RENAME TABLE {provider.Qualified(table)} TO {provider.Qualified(table with{Name=name})};",
            DatabaseKind.PostgreSQL=>$"ALTER TABLE {provider.Qualified(table)} RENAME TO {provider.Quote(name)};",
            DatabaseKind.SqlServer=>$"EXEC sys.sp_rename @objname={DataValues.String(provider.Qualified(table),kind)}, @newname={DataValues.String(name,kind)}, @objtype=N'OBJECT';",
            _=>throw new NotSupportedException("TDengine 当前不支持表重命名，请新建表并迁移数据。")};
    }
    public static string DropTable(IDbProvider provider,DbObject table)=>$"DROP {(table.Kind=="超级表"?"STABLE":"TABLE")} {provider.Qualified(table)};";
    public static string DropDatabase(IDbProvider provider,string name)=>$"DROP DATABASE {provider.Quote(Name(name))};";
    public static string RenameDatabase(IDbProvider provider,DatabaseKind kind,string name,string newName)
    {
        name=Name(name);newName=Name(newName);if(name==newName)throw new ArgumentException("新名称与原名称相同。");
        return kind switch{
            DatabaseKind.PostgreSQL=>$"ALTER DATABASE {provider.Quote(name)} RENAME TO {provider.Quote(newName)};",
            DatabaseKind.SqlServer=>$"ALTER DATABASE {provider.Quote(name)} MODIFY NAME = {provider.Quote(newName)};",
            _=>throw new NotSupportedException("当前数据库类型不支持直接重命名，请修改字符集或新建数据库。")};
    }
    public static string AlterDatabaseCharset(IDbProvider provider,string name,string charset,string collation)
    {
        name=Name(name);charset=Identifier(charset);collation=Identifier(collation);
        return $"ALTER DATABASE {provider.Quote(name)} CHARACTER SET {charset} COLLATE {collation};";
    }
    public static string DeleteAll(IDbProvider provider,DbObject table)=>$"DELETE FROM {provider.Qualified(table)};";
    public static string TruncateTable(IDbProvider provider,DatabaseKind kind,DbObject table)=>kind switch{
        DatabaseKind.TDengine=>throw new NotSupportedException("TDengine 不支持 TRUNCATE，请使用 DELETE 或按时间范围清理数据。"),
        DatabaseKind.PostgreSQL=>$"TRUNCATE TABLE {provider.Qualified(table)} RESTART IDENTITY;",
        _=>$"TRUNCATE TABLE {provider.Qualified(table)};"};
    public static string CopyTable(IDbProvider provider,DatabaseKind kind,DbObject source,DbObject target,bool withData)
    {
        var from=provider.Qualified(source);var to=provider.Qualified(target);
        return kind switch{
            DatabaseKind.MySQL=>withData?$"CREATE TABLE {to} LIKE {from};\nINSERT INTO {to} SELECT * FROM {from};":$"CREATE TABLE {to} LIKE {from};",
            DatabaseKind.PostgreSQL=>withData?$"CREATE TABLE {to} (LIKE {from} INCLUDING ALL);\nINSERT INTO {to} SELECT * FROM {from};":$"CREATE TABLE {to} (LIKE {from} INCLUDING ALL);",
            DatabaseKind.SqlServer=>withData?$"SELECT * INTO {to} FROM {from};":$"SELECT TOP 0 * INTO {to} FROM {from};",
            _=>throw new NotSupportedException("TDengine 请使用导出与导入向导复制表。")};
    }
    public static string? Maintenance(IDbProvider provider,DatabaseKind kind,DbObject table,string action)
    {
        if(kind==DatabaseKind.PostgreSQL)return action switch{ "vacuum"=>$"VACUUM {provider.Qualified(table)};", "analyze"=>$"ANALYZE {provider.Qualified(table)};", _=>null};
        if(kind==DatabaseKind.SqlServer)return action=="statistics"?$"UPDATE STATISTICS {provider.Qualified(table)};":null;
        if(kind!=DatabaseKind.MySQL)return null;
        var keyword=action switch{ "analyze"=>"ANALYZE", "check"=>"CHECK", "optimize"=>"OPTIMIZE", "repair"=>"REPAIR", _=>throw new ArgumentException("未知的表维护操作。")};
        return $"{keyword} TABLE {provider.Qualified(table)};";
    }
    private static string Identifier(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||!value.All(c=>char.IsLetterOrDigit(c)||c=='_'))throw new ArgumentException("字符集与排序规则只能包含字母、数字和下划线。");
        return value.Trim();
    }
}
