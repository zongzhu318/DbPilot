using System.Text.RegularExpressions;
using DbStudio.Models;

namespace DbStudio.Services;
public sealed record SqlCompletionContext(int Start,string Prefix,string Qualifier,string Statement,bool TablePosition,bool ColumnPosition=false)
{
    public static bool IsIdentifier(char c)=>char.IsLetterOrDigit(c)||c is '_' or '$';
    // Mask lexical regions without changing offsets. Semicolons inside literals are not statement boundaries.
    public static SqlCompletionContext? Read(string sql,int caret)
    {
        caret=Math.Clamp(caret,0,sql.Length);var masked=sql.ToCharArray();bool blocked=false;
        for(int i=0;i<sql.Length;){
            int start=i,end=i+1;bool ignore=false,quotedIdentifier=false,closed=false;
            if(i+1<sql.Length&&sql[i]=='-'&&sql[i+1]=='-'){end=sql.IndexOf('\n',i);closed=end>=0;if(end<0)end=sql.Length;ignore=true;}
            else if(i+1<sql.Length&&sql[i]=='/'&&sql[i+1]=='*'){
                int depth=1;end=i+2;while(end<sql.Length&&depth>0){if(end+1<sql.Length&&sql[end]=='/'&&sql[end+1]=='*'){depth++;end+=2;}else if(end+1<sql.Length&&sql[end]=='*'&&sql[end+1]=='/'){depth--;end+=2;}else end++;}closed=depth==0;ignore=true;
            }
            else if(sql[i] is '\'' or '"' or '`' or '['){
                char close=sql[i]=='['?']':sql[i];quotedIdentifier=sql[i]!='\'';end=i+1;
                while(end<sql.Length){if(sql[end]=='\\'&&!quotedIdentifier){end=Math.Min(end+2,sql.Length);continue;}if(sql[end++]==close){if(end<sql.Length&&sql[end]==close){end++;continue;}closed=true;break;}}ignore=true;
            }
            else if(sql[i]=='$'){
                var match=Regex.Match(sql[i..],@"^\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$");
                if(match.Success){int closing=sql.IndexOf(match.Value,i+match.Length,StringComparison.Ordinal);closed=closing>=0;end=closing<0?sql.Length:closing+match.Length;ignore=true;}
            }
            if(ignore){
                if(caret>start&&(caret<end||caret==end&&(!closed||sql[start]=='-')))blocked=true;
                for(int k=start;k<end;k++)if(!quotedIdentifier||sql[k]==';')masked[k]=' ';
            }
            i=end;
        }
        if(blocked)return null;
        int word=caret;while(word>0&&IsIdentifier(sql[word-1]))word--;
        string before=new(masked,0,word);int statementStart=before.LastIndexOf(';')+1;
        int statementEnd=Array.IndexOf(masked,';',caret);if(statementEnd<0)statementEnd=sql.Length;
        var qualifier=Regex.Match(before,@"([\p{L}_][\p{L}\p{N}_$]*)\s*\.\s*$");
        bool table=Regex.IsMatch(before[statementStart..],@"\b(FROM|JOIN|UPDATE|INTO|TABLE)\s+(?:[\p{L}_][\p{L}\p{N}_$]*\s*\.\s*)?$",RegexOptions.IgnoreCase);
        var clauses=Regex.Matches(before[statementStart..],@"\b(SELECT|FROM|JOIN|UPDATE|INTO|TABLE|VALUES|WHERE|ON|SET|HAVING|RETURNING|ORDER\s+BY|GROUP\s+BY)\b",RegexOptions.IgnoreCase);
        string clause=clauses.LastOrDefault()?.Value.ToUpperInvariant()??"";
        if(clause is "FROM" or "JOIN"&&Regex.IsMatch(before[statementStart..],@",\s*$"))table=true;
        bool column=!table&&clause is "SELECT" or "WHERE" or "ON" or "SET" or "HAVING" or "RETURNING" or "ORDER BY" or "GROUP BY";
        if(clause=="INTO"&&Regex.IsMatch(before[statementStart..],@"\bINTO\s+[^()]+\([^)]*$",RegexOptions.IgnoreCase))column=true;
        return new(word,sql[word..caret],qualifier.Success?qualifier.Groups[1].Value:"",sql[statementStart..statementEnd],table,column);
    }
    public static IEnumerable<string> Keywords(DatabaseKind kind)
    {
        var common="SELECT FROM WHERE ORDER BY GROUP HAVING JOIN LEFT RIGHT INNER OUTER CROSS FULL ON AS AND OR NOT NULL IS INSERT INTO VALUES UPDATE SET DELETE CREATE ALTER DROP TABLE VIEW INDEX DISTINCT UNION ALL DESC ASC COUNT SUM AVG MIN MAX COALESCE CASE WHEN THEN ELSE END EXISTS IN BETWEEN LIKE WITH OVER PARTITION ROW_NUMBER CAST EXPLAIN BEGIN COMMIT ROLLBACK";
        var dialect=kind switch{DatabaseKind.SqlServer=>" TOP OFFSET FETCH NEXT ROWS ONLY GETDATE ISNULL NVARCHAR",DatabaseKind.PostgreSQL=>" LIMIT OFFSET RETURNING ILIKE NOW JSONB SERIAL BOOLEAN TRUE FALSE",DatabaseKind.MySQL=>" LIMIT OFFSET SHOW DESCRIBE IFNULL NOW AUTO_INCREMENT DATABASE",_=>" LIMIT OFFSET STABLE TAGS INTERVAL SLIDING FILL TBNAME NOW TTL"};
        return (common+dialect).Split(' ',StringSplitOptions.RemoveEmptyEntries).Distinct();
    }
}
