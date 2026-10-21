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
            var token=match.Value;tokens.Append(token.StartsWith('\'')||token.StartsWith('"')||token.StartsWith('`')||token.StartsWith('[')||token.StartsWith("--")||token.StartsWith("/*")||token.StartsWith('$')?new string(' ',token.Length):token);
        }
        return Regex.IsMatch(tokens.ToString(),@"\b(DROP|TRUNCATE)\b",RegexOptions.IgnoreCase);
    }
    // Layout formatter without a full parser: keyword casing, clause breaks and indentation outside literals and comments.
    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var tokens=Tokenize(sql);
        var output=new StringBuilder(sql.Length+64);
        var scopes=new Stack<Scope>();
        var parens=new Stack<bool>();
        var scope=new Scope();
        SqlToken? previous=null;
        var newline=Environment.NewLine;
        var lineIndent=0;
        var atLineStart=true;
        var rawLine=false;
        var createPending=false;
        var expectCreateTable=false;

        void Space(){if(!atLineStart&&output.Length>0&&output[^1]!=' ')output.Append(' ');}
        void SetIndent(int indent)
        {
            if(!atLineStart)return;
            while(output.Length>0&&output[^1]==' ')output.Length--;
            if(output.Length>0)output.Append(' ',indent);
            lineIndent=indent;
        }
        void Break(int indent)
        {
            if(atLineStart){SetIndent(indent);return;}
            while(output.Length>0&&char.IsWhiteSpace(output[^1]))output.Length--;
            output.Append(newline);output.Append(' ',indent);
            lineIndent=indent;atLineStart=true;
        }
        void Write(string text){output.Append(text);atLineStart=false;}
        bool IsTightValue(SqlToken token)=>previous is {Kind:TokenKind.Symbol} p&&
            (p.Text is "(" or "."||(!token.SpaceBefore&&p.Text is "@" or "$" or ":" or "#"));
        bool IsTightSymbol(SqlToken token)=>previous is {Kind:TokenKind.Symbol} p
            ?p.Text is "(" or "."||!token.SpaceBefore
            :!token.SpaceBefore&&token.Text is ":" or "~" or "@" or "$" or "#";
        void WriteWordOrLiteral(string text,SqlToken token)
        {
            var tight=IsTightValue(token);
            if(!tight&&(token.Kind is TokenKind.String or TokenKind.Quoted)&&previous is {Kind:TokenKind.Word} word&&!token.SpaceBefore&&IsStringPrefix(word.Text))tight=true;
            if(!tight)Space();
            output.Append(text);atLineStart=false;
        }
        void WriteSymbol(string text,SqlToken token)
        {
            if(!IsTightSymbol(token))Space();
            output.Append(text);atLineStart=false;
        }
        void WriteOpenParen(SqlToken token,bool structured)
        {
            if(!atLineStart)
            {
                if(structured)Space();
                else if(previous is {Kind:TokenKind.Word} word&&KeywordRegex().IsMatch(word.Text))Space();
                else if(previous is {Kind:TokenKind.Symbol} p&&p.Text==")")Space();
                else if(token.SpaceBefore&&previous is not {Kind:TokenKind.Symbol})Space();
            }
            output.Append('(');atLineStart=false;
        }
        void ResetStatement(){scope.Clause=Clause.None;scope.Between=0;createPending=false;expectCreateTable=false;}

        for(int i=0;i<tokens.Count;i++)
        {
            var token=tokens[i];
            var text=token.Text;
            if(rawLine)
            {
                if(!token.NewlineBefore)
                {
                    if(!atLineStart&&token.SpaceBefore)Space();
                    output.Append(text);atLineStart=false;previous=token;
                    continue;
                }
                rawLine=false;Break(0);ResetStatement();
            }
            if(token.Kind==TokenKind.LineComment)
            {
                if(!atLineStart)Space();
                output.Append(text);atLineStart=false;
                Break(lineIndent);
                previous=token;
                continue;
            }
            if(token.Kind==TokenKind.BlockComment)
            {
                if(!atLineStart)Space();
                output.Append(text);atLineStart=false;previous=token;
                continue;
            }
            if(token.Kind==TokenKind.Number)
            {
                WriteWordOrLiteral(text,token);
                previous=token;
                continue;
            }
            if(token.Kind==TokenKind.Word)
            {
                var upper=text.ToUpperInvariant();
                var next=NextWord(tokens,i+1);
                var next2=NextWord(tokens,i+1,2);
                var lineStartish=i==0||token.NewlineBefore||previous is {Text:";"};
                if(upper=="GO"&&scopes.Count==0&&scope.Nesting==0&&lineStartish)
                {
                    Break(0);Write("GO");rawLine=true;ResetStatement();
                    previous=token;
                    continue;
                }
                if(upper=="DELIMITER"&&scopes.Count==0&&scope.Nesting==0&&lineStartish)
                {
                    Break(0);Write(text);rawLine=true;ResetStatement();
                    previous=token;
                    continue;
                }
                if(scope.Nesting==0)
                {
                    switch(upper)
                    {
                        case "SELECT":Break(scope.ContentIndent);scope.Clause=Clause.Select;scope.Between=0;break;
                        case "FROM":
                            if(previous?.Kind!=TokenKind.Word||!previous.Value.Text.Equals("DELETE",StringComparison.OrdinalIgnoreCase))
                                Break(scope.ContentIndent);
                            scope.Clause=Clause.From;break;
                        case "WHERE":Break(scope.ContentIndent);scope.Clause=Clause.Where;scope.Between=0;break;
                        case "HAVING":Break(scope.ContentIndent);scope.Clause=Clause.Having;scope.Between=0;break;
                        case "GROUP" or "ORDER" when next=="BY":Break(scope.ContentIndent);scope.Clause=upper=="GROUP"?Clause.Group:Clause.Order;break;
                        case "LIMIT" or "OFFSET" or "FETCH":Break(scope.ContentIndent);scope.Clause=Clause.Limit;break;
                        case "SET":Break(scope.ContentIndent);scope.Clause=Clause.Set;scope.Between=0;break;
                        case "VALUES":Break(scope.ContentIndent);scope.Clause=Clause.Values;scope.Between=0;break;
                        case "RETURNING":Break(scope.ContentIndent);scope.Clause=Clause.Returning;break;
                        case "WINDOW":Break(scope.ContentIndent);scope.Clause=Clause.None;break;
                        case "UNION" or "INTERSECT" or "EXCEPT" or "MINUS":Break(scope.ContentIndent);scope.Clause=Clause.None;scope.Between=0;break;
                        case "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "ALTER" or "DROP" or "TRUNCATE":
                            Break(scope.ContentIndent);scope.Clause=Clause.None;break;
                        case "CREATE":Break(scope.ContentIndent);scope.Clause=Clause.None;break;
                        case "JOIN" or "STRAIGHT_JOIN":
                            if(upper=="STRAIGHT_JOIN"||previous?.Kind!=TokenKind.Word||!IsJoinModifier(previous.Value.Text))
                                Break(scope.ContentIndent);
                            scope.Clause=Clause.Join;break;
                        case "LEFT" or "RIGHT" or "INNER" or "OUTER" or "FULL" or "CROSS" or "NATURAL" or "LATERAL"
                            when (next=="JOIN"||next=="APPLY"||next=="OUTER"&&next2=="JOIN")
                                &&(previous?.Kind!=TokenKind.Word||!IsJoinModifier(previous.Value.Text)):
                            Break(scope.ContentIndent);scope.Clause=Clause.Join;break;
                        case "ON" when scope.Clause==Clause.Join:
                            Break(scope.ContentIndent+4);scope.Clause=Clause.On;scope.Between=0;break;
                        case "AND":
                            if(scope.Between>0){scope.Between--;break;}
                            if(scope.Clause is Clause.Where or Clause.Having or Clause.On)Break(scope.ContentIndent+4);
                            break;
                        case "OR":
                            if(scope.Clause is Clause.Where or Clause.Having or Clause.On)Break(scope.ContentIndent+4);
                            break;
                        case "BETWEEN":scope.Between++;break;
                    }
                }
                if(upper=="CREATE")createPending=true;
                else if(upper=="SELECT")expectCreateTable=false;
                else if(upper=="TABLE")
                {
                    if(createPending){expectCreateTable=true;createPending=false;}
                }
                else if(upper is not ("TEMP" or "TEMPORARY" or "OR" or "REPLACE" or "UNIQUE" or "IF" or "NOT" or "EXISTS"))createPending=false;
                WriteWordOrLiteral(KeywordRegex().IsMatch(text)?upper:text,token);
                previous=token;
                continue;
            }
            if(token.Kind is TokenKind.String or TokenKind.Quoted)
            {
                WriteWordOrLiteral(text,token);
                previous=token;
                continue;
            }
            switch(text)
            {
                case "(":
                    var structured=NextWord(tokens,i+1) is "SELECT" or "WITH" or "VALUES" or "TABLE"||(expectCreateTable&&scope.Nesting==0);
                    WriteOpenParen(token,structured);
                    parens.Push(structured);
                    if(structured)
                    {
                        var child=new Scope{Structured=true,CreateTable=expectCreateTable,BaseIndent=lineIndent,ContentIndent=lineIndent+4};
                        expectCreateTable=false;scopes.Push(scope);scope=child;
                        Break(scope.ContentIndent);
                    }
                    else
                    {
                        scope.Nesting++;
                    }
                    break;
                case ")":
                    if(parens.Count>0&&parens.Pop())
                    {
                        Break(scope.BaseIndent);
                        scope=scopes.Count>0?scopes.Pop():scope;
                        output.Append(')');atLineStart=false;
                    }
                    else
                    {
                        if(scope.Nesting>0)scope.Nesting--;
                        output.Append(')');atLineStart=false;
                    }
                    break;
                case ",":
                    output.Append(',');atLineStart=false;
                    if(scope.Nesting==0)
                    {
                        if(scope.Structured&&scope.CreateTable)Break(scope.ContentIndent);
                        else if(scope.Clause is Clause.Select or Clause.Set or Clause.Values)Break(scope.ContentIndent+4);
                    }
                    break;
                case ";":
                    output.Append(';');atLineStart=false;
                    if(scopes.Count==0&&scope.Nesting==0)
                    {
                        Break(0);ResetStatement();
                    }
                    break;
                case ".":
                    output.Append('.');atLineStart=false;
                    break;
                default:
                    WriteSymbol(text,token);
                    break;
            }
            previous=token;
        }
        while(output.Length>0&&char.IsWhiteSpace(output[^1]))output.Length--;
        return output.ToString();
    }

    private enum Clause { None, Select, From, Where, Group, Order, Having, Limit, Set, Values, Join, On, Returning }
    private enum TokenKind { Word, Number, String, Quoted, LineComment, BlockComment, Symbol }
    private readonly record struct SqlToken(string Text,TokenKind Kind,bool SpaceBefore,bool NewlineBefore);
    private sealed class Scope
    {
        public bool Structured;
        public bool CreateTable;
        public int BaseIndent;
        public int ContentIndent;
        public int Nesting;
        public Clause Clause;
        public int Between;
    }

    private static List<SqlToken> Tokenize(string sql)
    {
        var tokens=new List<SqlToken>();
        var space=false;var newline=false;
        foreach(Match match in TokenRegex().Matches(sql))
        {
            var text=match.Value;
            if(text.Length==1&&char.IsWhiteSpace(text[0]))
            {
                space=true;if(text[0] is '\r' or '\n')newline=true;
                continue;
            }
            var kind=text.StartsWith("--")?TokenKind.LineComment
                :text.StartsWith("/*")?TokenKind.BlockComment
                :text[0] is '"' or '`' or '['?TokenKind.Quoted
                :text[0]=='\''||text[0]=='$'&&text.Length>1?TokenKind.String
                :char.IsLetter(text[0])||text[0]=='_'?TokenKind.Word
                :char.IsDigit(text[0])||text[0]=='.'&&text.Length>1?TokenKind.Number
                :TokenKind.Symbol;
            tokens.Add(new(text,kind,space,newline));
            space=false;newline=false;
        }
        return tokens;
    }

    private static string? NextWord(List<SqlToken> tokens,int index,int ordinal=1)
    {
        for(var i=index;i<tokens.Count;i++)
        {
            var token=tokens[i];
            if(token.Kind==TokenKind.LineComment||token.Kind==TokenKind.BlockComment)continue;
            if(token.Kind!=TokenKind.Word)return null;
            if(--ordinal==0)return token.Text.ToUpperInvariant();
        }
        return null;
    }

    private static bool IsStringPrefix(string text)=>
        text.Length==1&&(text[0] is 'N' or 'n' or 'E' or 'e' or 'X' or 'x' or 'B' or 'b')||text.StartsWith('_');

    private static bool IsJoinModifier(string text)=>
        text.ToUpperInvariant() is "LEFT" or "RIGHT" or "INNER" or "OUTER" or "FULL" or "CROSS" or "NATURAL" or "LATERAL";

    public static string FormatDdl(string sql) => Format(sql);
    [GeneratedRegex(@"\$(?<tag>[A-Za-z_][A-Za-z_0-9]*)?\$[\s\S]*?\$\k<tag>\$|'(?:''|[^'])*'|""(?:""""|[^""])*""|`(?:``|[^`])*`|\[(?:\]\]|[^\]])*\]|--[^\r\n]*|/\*[\s\S]*?\*/|0[xX][0-9A-Fa-f]+|0[bB][01]+|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[A-Za-z_][A-Za-z_0-9]*|[\s\S]")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"^(select|from|where|order|by|group|having|join|left|right|inner|outer|full|cross|natural|straight_join|on|as|and|or|not|null|is|insert|into|values|update|set|delete|create|table|index|limit|offset|fetch|next|rows|only|distinct|union|all|desc|asc|case|when|then|else|end|with|recursive|returning|using|between|like|ilike|in|exists|top|percent|ties|window|partition|over|range|preceding|following|unbounded|current|row|first|last|nulls|primary|key|foreign|references|unique|check|default|constraint|add|modify|alter|drop|truncate|column|database|schema|view|procedure|function|trigger|begin|declare|if|while|loop|for|each|return|replace|ignore|duplicate|conflict|do|nothing|explain|analyze|describe|show|use|transaction|commit|rollback|savepoint|grant|revoke|merge|matched|lateral|apply|temp|temporary|of|no|lock|share|nowait|skip|locked|escape|collate|character|cascade|restrict|without|zone|interval|into|outfile|dumpfile|foreach|repeat|until|leave|iterate|signal|resignal|call|handler|condition|continue|exit|open|close|fetch|deallocate|prepare|execute|delimiter)$",RegexOptions.IgnoreCase)]
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
