using System.Text.RegularExpressions;
namespace DbPilot.Services;
public sealed record SqlTableReference(string Name,string Schema,string Alias);
public static partial class SqlReferences
{
    // Completion aid only: subquery scopes and CTE semantics remain the server's responsibility.
    public static IReadOnlyList<SqlTableReference> Read(string sql)
    {
        var text=Ignored().Replace(sql,m=>new string(' ',m.Length));var result=new List<SqlTableReference>();
        for(int offset=0;offset<text.Length;){
            var match=Reference().Match(text,offset);if(!match.Success)break;offset=match.Groups[1].Index+match.Groups[1].Length;
            var parts=Identifier().Matches(match.Groups[1].Value).Select(m=>Unquote(m.Value)).ToArray();if(parts.Length==0)continue;
            var alias=match.Groups[2].Value;if(Reserved().IsMatch(alias))alias="";
            result.Add(new(parts[^1],parts.Length>1?parts[^2]:"",alias.Length>0?Unquote(alias):parts[^1]));
        }
        return result.Distinct().Take(12).ToArray();
    }
    private static string Unquote(string s)=>s.StartsWith('[')?s[1..^1].Replace("]]","]"):s.StartsWith('"')?s[1..^1].Replace("\"\"","\""):s.StartsWith('`')?s[1..^1].Replace("``","`"):s;
    [GeneratedRegex(@"'(?:''|[^'])*'|--[^\r\n]*|/\*[\s\S]*?\*/")]private static partial Regex Ignored();
    [GeneratedRegex("\\b(?:FROM|JOIN|UPDATE|INTO)\\s+((?:\\[(?:\\]\\]|[^\\]])+\\]|\"(?:\"\"|[^\"])+\"|`(?:``|[^`])+`|[\\p{L}_][\\p{L}\\p{N}_$]*)(?:\\s*\\.\\s*(?:\\[(?:\\]\\]|[^\\]])+\\]|\"(?:\"\"|[^\"])+\"|`(?:``|[^`])+`|[\\p{L}_][\\p{L}\\p{N}_$]*)){0,2})(?:\\s+(?:AS\\s+)?([\\p{L}_][\\p{L}\\p{N}_$]*))?",RegexOptions.IgnoreCase)]private static partial Regex Reference();
    [GeneratedRegex("\\[(?:\\]\\]|[^\\]])+\\]|\"(?:\"\"|[^\"])+\"|`(?:``|[^`])+`|[\\p{L}_][\\p{L}\\p{N}_$]*")]private static partial Regex Identifier();
    [GeneratedRegex(@"^(WHERE|JOIN|LEFT|RIGHT|INNER|OUTER|CROSS|FULL|ON|GROUP|ORDER|HAVING|LIMIT|OFFSET|FETCH|UNION|SET|VALUES|RETURNING|WINDOW|INTERVAL|SLIDING|FILL)$",RegexOptions.IgnoreCase)]private static partial Regex Reserved();
}
