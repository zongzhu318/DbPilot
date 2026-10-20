using System.Text;
using System.Text.RegularExpressions;
using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.Services;
public static partial class SqlTools
{
    public static bool RequiresConfirmation(string sql)
    {
        var tokens=new StringBuilder();foreach(Match match in TokenRegex().Matches(sql)){
            var token=match.Value;tokens.Append(token.StartsWith('\'')||token.StartsWith('"')||token.StartsWith('`')||token.StartsWith('[')||token.StartsWith("--")||token.StartsWith("/*")?new string(' ',token.Length):token);
        }
        return Regex.IsMatch(tokens.ToString(),@"\b(DROP|TRUNCATE)\b",RegexOptions.IgnoreCase);
    }
    // Only reformat keyword tokens outside literals, quoted identifiers and comments.
    public static string Format(string sql)
    {
        var result = new StringBuilder();
        foreach (Match m in TokenRegex().Matches(sql))
        {
            var token = m.Value;
            if (KeywordRegex().IsMatch(token)) token = token.ToUpperInvariant();
            result.Append(token);
        }
        return result.ToString();
    }
    public static string FormatDdl(string sql)
    {
        var result=new StringBuilder();int depth=0;bool space=false;
        void NewLine(int indent){while(result.Length>0&&char.IsWhiteSpace(result[^1]))result.Length--;result.AppendLine();result.Append(' ',indent);space=false;}
        foreach(Match match in TokenRegex().Matches(Format(sql))){
            var token=match.Value;
            if(string.IsNullOrWhiteSpace(token)){space=true;continue;}
            if(token=="("){
                if(space&&result.Length>0)result.Append(' ');
                result.Append(token);depth++;space=false;
                if(depth==1)NewLine(4);
            }else if(token==")"){
                if(depth==1)NewLine(0);
                result.Append(token);depth=Math.Max(0,depth-1);space=false;
            }else if(token==","&&depth==1){result.Append(',');NewLine(4);}
            else if(token==";"&&depth==0){result.Append(';');NewLine(0);}
            else{
                if(space&&result.Length>0&&!char.IsWhiteSpace(result[^1]))result.Append(' ');
                result.Append(token);space=false;
                if(token.StartsWith("--"))NewLine(depth>0?4:0);
            }
        }
        return result.ToString().Trim();
    }
    [GeneratedRegex(@"'(?:''|[^'])*'|""(?:""""|[^""])*""|`(?:``|[^`])*`|\[(?:\]\]|[^\]])*\]|--[^\r\n]*|/\*[\s\S]*?\*/|[A-Za-z_][A-Za-z_0-9]*|[\s\S]")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"^(select|from|where|order|by|group|having|join|left|right|inner|outer|on|as|and|or|not|null|is|insert|into|values|update|set|delete|create|table|index|limit|offset|fetch|next|rows|only|distinct|union|all|desc|asc|case|when|then|else|end)$",RegexOptions.IgnoreCase)]
    private static partial Regex KeywordRegex();
    public static string InsertTemplate(IDbProvider provider, DbObject table,IReadOnlyList<ColumnInfo> columns) =>
        $"INSERT INTO {provider.Qualified(table)} ({string.Join(", ",columns.Where(c=>!c.IsTag).Select(c=>provider.Quote(c.Name)))})\nVALUES ({string.Join(", ",columns.Where(c=>!c.IsTag).Select(_=>"NULL"))});";
    public static string CreateTemplate(IDbProvider provider, DbObject table,IReadOnlyList<ColumnInfo> columns)
    {
        var fields = columns.Where(c=>!c.IsTag).Select(c=>$"  {provider.Quote(c.Name)} {c.Type}" + (c.Nullable ? "" : " NOT NULL"));
        var tags = columns.Where(c=>c.IsTag).ToArray();
        return "-- 根据元数据生成的建表模板；执行前请核对默认值、约束及数据库专属选项。\n" +
            $"CREATE TABLE {provider.Qualified(table)} (\n{string.Join(",\n",fields)}\n)" +
            (tags.Length == 0 ? "" : $" TAGS ({string.Join(", ",tags.Select(c=>$"{provider.Quote(c.Name)} {c.Type}"))})") + ";";
    }
    public static string Display(object? value) => value switch { null or DBNull => "NULL", byte[] b => "0x" + Convert.ToHexString(b.AsSpan(0,Math.Min(64,b.Length))) + (b.Length>64 ? "…" : ""), DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff"), _ => Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture) ?? "" };
    public static string CsvCell(object? value) { var text = value is null ? "" : Display(value); return "\"" + text.Replace("\"","\"\"") + "\""; }
}
