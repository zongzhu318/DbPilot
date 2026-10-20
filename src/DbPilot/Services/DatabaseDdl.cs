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
}
