using DbPilot.Models;

namespace DbPilot.Services;
public static class LargeColumns
{
    public const int PreviewLength = 256;
    private const int DeclaredLengthLimit = 1024;
    public static IReadOnlyList<int> PreviewIndexes(IReadOnlyList<ColumnInfo> columns,DatabaseKind kind)
    {
        var indexes=new List<int>();
        for(int i=0;i<columns.Count;i++)
            if(!columns[i].PrimaryKey && IsLarge(columns[i].Type,kind))indexes.Add(i);
        return indexes;
    }
    public static bool IsLarge(string type,DatabaseKind kind)
    {
        var (name,length,unbounded)=Parse(type);
        var longBounded=unbounded || length>DeclaredLengthLimit;
        return kind switch{
            DatabaseKind.MySQL=>name is "tinytext" or "text" or "mediumtext" or "longtext" or "json" or "tinyblob" or "blob" or "mediumblob" or "longblob" || (name is "varchar" or "varbinary") && longBounded,
            DatabaseKind.PostgreSQL=>name is "text" or "bytea" or "xml" or "json" or "jsonb" or "tsvector" || (name is "character varying" or "character") && longBounded,
            DatabaseKind.SqlServer=>name is "text" or "ntext" or "image" or "xml" || (name is "varchar" or "nvarchar" or "varbinary" or "binary") && longBounded,
            DatabaseKind.TDengine=>(name is "varchar" or "nchar" or "varbinary" or "binary") && longBounded,
            _=>false
        };
    }
    // Types arrive as information_schema/format_type text, e.g. varchar(2000), nvarchar(max), character varying(40).
    private static (string Name,int Length,bool Unbounded) Parse(string type)
    {
        var text=type.Trim().ToLowerInvariant();
        var open=text.IndexOf('(');
        if(open<0)return(text,0,false);
        var close=text.IndexOf(')',open+1);
        var argument=close>open?text[(open+1)..close].Trim():"";
        var name=text[..open].Trim();
        if(argument=="max")return(name,0,true);
        return int.TryParse(argument.Split(',')[0].Trim(),out var length) ? (name,length,false) : (name,0,false);
    }
}
