using System.Globalization;
using System.Text;
using System.Text.Json;
using DbStudio.Models;

namespace DbStudio.Services;
public static class DataValues
{
    public static string Literal(object? value,DatabaseKind kind)=>value switch{
        null or DBNull=>"NULL",bool b=>kind==DatabaseKind.PostgreSQL?(b?"TRUE":"FALSE"):(b?"1":"0"),
        byte[] bytes=>kind switch{DatabaseKind.PostgreSQL=>"decode('"+Convert.ToHexString(bytes)+"','hex')",DatabaseKind.MySQL=>"0x"+Convert.ToHexString(bytes),DatabaseKind.SqlServer=>"0x"+Convert.ToHexString(bytes),_=>String(Convert.ToHexString(bytes),kind)},
        DateTime d=>String(d.ToString(kind==DatabaseKind.MySQL?"yyyy-MM-dd HH:mm:ss.ffffff":"O",CultureInfo.InvariantCulture),kind),
        DateTimeOffset d=>String(d.ToString("O",CultureInfo.InvariantCulture),kind),
        float f when !float.IsFinite(f)=>String(f.ToString(CultureInfo.InvariantCulture),kind),double d when !double.IsFinite(d)=>String(d.ToString(CultureInfo.InvariantCulture),kind),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float=>Convert.ToString(value,CultureInfo.InvariantCulture)!,
        _=>String(Convert.ToString(value,CultureInfo.InvariantCulture)??"",kind)};
    public static string String(string text,DatabaseKind kind)=>kind switch{
        DatabaseKind.MySQL=>"CONVERT(0x"+Convert.ToHexString(Encoding.UTF8.GetBytes(text))+" USING utf8mb4)",
        DatabaseKind.PostgreSQL=>"E'"+text.Replace("\\","\\\\").Replace("'","''")+"'",
        DatabaseKind.SqlServer=>"N'"+text.Replace("'","''")+"'",
        _=>"'"+text.Replace("\\","\\\\").Replace("'","\\'")+"'"};
    public static object? ForColumn(object? value,ColumnInfo column,DatabaseKind kind)
    {
        if(value is JsonElement element)value=element.ValueKind switch{JsonValueKind.Null=>null,JsonValueKind.String=>element.GetString(),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>element.ToString()};
        if(value is null or DBNull){if(!column.Nullable && !column.IsGenerated)throw new FormatException($"字段 {column.Name} 不允许 NULL。");return null;}
        var type=column.Type.ToLowerInvariant();string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        try{
            if(kind==DatabaseKind.TDengine&&(type.StartsWith("binary")||type.StartsWith("nchar")||type.StartsWith("varchar")))return text;
            if(type.Contains("binary") || type is "bytea" or "image" or "rowversion" || type.Contains("blob"))return value is byte[]?value:Convert.FromBase64String(text);
            if(type=="uuid"||type=="uniqueidentifier")return Guid.Parse(text);
            if(type.Contains("timestamp")||type.Contains("datetime")||type=="date"){
                if(type.Contains("offset"))return DateTimeOffset.Parse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                var date=value is DateTime dt?dt:DateTime.Parse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                return kind==DatabaseKind.PostgreSQL ? type.Contains("with time zone")?date.ToUniversalTime():DateTime.SpecifyKind(date,DateTimeKind.Unspecified) : date;
            }
            if(type.StartsWith("time"))return value is TimeSpan?value:TimeSpan.Parse(text,CultureInfo.InvariantCulture);
            if(type is "bool" or "boolean" or "bit")return text.ToLowerInvariant() switch { "1" or "t" or "true"=>true,"0" or "f" or "false"=>false,_=>throw new FormatException("无效布尔值") };
            if(type.StartsWith("smallint")||type=="int2")return type.Contains("unsigned")?ushort.Parse(text,CultureInfo.InvariantCulture):short.Parse(text,CultureInfo.InvariantCulture);
            if(type.Contains("bigint")||type=="int8")return type.Contains("unsigned")?ulong.Parse(text,CultureInfo.InvariantCulture):long.Parse(text,CultureInfo.InvariantCulture);
            if(type=="integer"||type.StartsWith("int(")||type.StartsWith("int ")||type is "int" or "int4" || type.StartsWith("mediumint"))return type.Contains("unsigned")?uint.Parse(text,CultureInfo.InvariantCulture):int.Parse(text,CultureInfo.InvariantCulture);
            if(type.StartsWith("tinyint"))return value is bool flag ? (short)(flag?1:0) : short.Parse(text,CultureInfo.InvariantCulture);
            if(type.StartsWith("decimal")||type.StartsWith("numeric")||type.Contains("money"))return decimal.Parse(text,NumberStyles.Float,CultureInfo.InvariantCulture);
            if(type.StartsWith("real")||type.StartsWith("float")||type.StartsWith("double"))return double.Parse(text,CultureInfo.InvariantCulture);
            return value is string ? text : value;
        }catch(Exception ex) when(ex is FormatException or OverflowException){throw new FormatException($"字段 {column.Name} 的值无法转换为 {column.Type}。",ex);}
    }
}
