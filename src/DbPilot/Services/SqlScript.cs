using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DbPilot.Models;
using DbPilot.Providers;

namespace DbPilot.Services;
public sealed partial class SqlScript
{
    private readonly DatabaseKind _kind;
    private readonly StringBuilder _pending=new();
    private char _quote;
    private bool _escape;
    private int _commentDepth;
    private string? _dollar;
    private string _delimiter=";";
    private int _line;
    private int _start=1;
    public SqlScript(DatabaseKind kind)=>_kind=kind;
    private bool Plain=>_quote=='\0' && _commentDepth==0 && _dollar is null;
    public IReadOnlyList<ScriptBatch> Feed(string line)
    {
        if((long)_pending.Length+line.Length>64*1024*1024)throw new FormatException("单个 SQL 批次超过 64 MB，请拆分脚本中的大语句。");
        _line++;var batches=new List<ScriptBatch>();
        if(Plain){
            var trimmed=line.Trim();
            if(_kind==DatabaseKind.SqlServer && GoPattern().Match(trimmed) is {Success:true} go){
                int repeat=go.Groups[1].Success?int.Parse(go.Groups[1].Value):1;if(repeat is <1 or >1000)throw new FormatException("GO 重复次数必须为 1–1000。");
                if(HasSql(_pending.ToString()))for(int n=0;n<repeat;n++)batches.Add(new(_pending.ToString(),_start));_pending.Clear();_start=_line+1;return batches;
            }
            if(_kind==DatabaseKind.MySQL && trimmed.StartsWith("DELIMITER ",StringComparison.OrdinalIgnoreCase)){
                if(HasSql(_pending.ToString()))throw new FormatException("DELIMITER 必须位于语句之间。");
                _delimiter=trimmed[10..].Trim();if(_delimiter.Length is <1 or >16 || _delimiter.Any(char.IsWhiteSpace))throw new FormatException("DELIMITER 无效。");_pending.Clear();_start=_line+1;return batches;
            }
        }
        bool lineComment=false;
        for(int i=0;i<line.Length;i++){
            char c=line[i];char next=i+1<line.Length?line[i+1]:'\0';
            if(lineComment){_pending.Append(c);continue;}
            if(_dollar is not null){if(line.AsSpan(i).StartsWith(_dollar)){_pending.Append(_dollar);i+=_dollar.Length-1;_dollar=null;}else _pending.Append(c);continue;}
            if(_commentDepth>0){_pending.Append(c);if(c=='/'&&next=='*'){_commentDepth++;_pending.Append(next);i++;}else if(c=='*'&&next=='/'){_commentDepth--;_pending.Append(next);i++;}continue;}
            if(_quote!='\0'){
                _pending.Append(c);
                if(_escape&&c=='\\'&&i+1<line.Length){_pending.Append(next);i++;continue;}
                if(c==_quote){if(next==_quote){_pending.Append(next);i++;}else _quote='\0';}continue;
            }
            if(c=='-'&&next=='-'&&(_kind!=DatabaseKind.MySQL||i+2==line.Length||char.IsWhiteSpace(line[i+2])) || c=='#'&&_kind==DatabaseKind.MySQL){lineComment=true;_pending.Append(c);continue;}
            if(c=='/'&&next=='*'){_commentDepth=1;_pending.Append("/*");i++;continue;}
            if(c is '\'' or '"' or '`' || c=='['&&_kind==DatabaseKind.SqlServer){
                _quote=c=='['?']':c;_escape=c=='\''&&(_kind is DatabaseKind.MySQL or DatabaseKind.TDengine || _kind==DatabaseKind.PostgreSQL&&i>0&&line[i-1] is 'E' or 'e');_pending.Append(c);continue;
            }
            if(c=='$'&&_kind==DatabaseKind.PostgreSQL&&(i==0||!char.IsLetterOrDigit(line[i-1])&&line[i-1] is not ('_' or '$'))){var match=DollarPattern().Match(line[i..]);if(match.Success){_dollar=match.Value;_pending.Append(_dollar);i+=_dollar.Length-1;continue;}}
            if(_kind!=DatabaseKind.SqlServer && line.AsSpan(i).StartsWith(_delimiter)){
                if(HasSql(_pending.ToString()))batches.Add(new(_pending.ToString(),_start));_pending.Clear();i+=_delimiter.Length-1;_start=_line;continue;
            }
            _pending.Append(c);
        }
        _pending.Append('\n');if(!HasSql(_pending.ToString()))_start=_line+1;return batches;
    }
    public ScriptBatch? Finish(){if(!Plain)throw new FormatException($"第 {_start} 行开始的字符串或注释未闭合。");return HasSql(_pending.ToString())?new(_pending.ToString(),_start):null;}
    private bool HasSql(string text)
    {
        int depth=0;bool line=false;
        for(int i=0;i<text.Length;i++){
            char c=text[i],next=i+1<text.Length?text[i+1]:'\0';
            if(line){if(c=='\n')line=false;continue;}
            if(depth>0){if(c=='/'&&next=='*'){depth++;i++;}else if(c=='*'&&next=='/'){depth--;i++;}continue;}
            if(c=='-'&&next=='-'&&(_kind!=DatabaseKind.MySQL||i+2==text.Length||char.IsWhiteSpace(text[i+2])) || c=='#'&&_kind==DatabaseKind.MySQL){line=true;continue;}
            if(c=='/'&&next=='*'){if(_kind==DatabaseKind.MySQL&&i+2<text.Length&&text[i+2]=='!')return true;depth++;i++;continue;}
            if(!char.IsWhiteSpace(c))return true;
        }
        return false;
    }
    public static async IAsyncEnumerable<ScriptBatch> ReadAsync(TextReader reader,DatabaseKind kind,[EnumeratorCancellation] CancellationToken ct)
    {
        var parser=new SqlScript(kind);string? line;while((line=await reader.ReadLineAsync(ct)) is not null){ct.ThrowIfCancellationRequested();foreach(var batch in parser.Feed(line))yield return batch;}
        if(parser.Finish() is {} last)yield return last;
    }
    [GeneratedRegex(@"^GO(?:\s+(\d+))?(?:\s*--.*)?$",RegexOptions.IgnoreCase)]private static partial Regex GoPattern();
    [GeneratedRegex(@"^\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$")]private static partial Regex DollarPattern();
}
public static class ScriptRunner
{
    public static async Task<IReadOnlyList<ScriptOutcome>> RunAsync(IDbProvider provider,TextReader reader,DatabaseKind kind,bool continueOnError,int limit,IProgress<TransferProgress>? progress,CancellationToken ct,bool capReadOnlyQueries=false)
    {
        var outcomes=new List<ScriptOutcome>();int number=0;
        await foreach(var batch in SqlScript.ReadAsync(reader,kind,ct)){
            number++;ScriptOutcome outcome;
            try{outcome=new(number,batch.Line,await provider.QueryMultipleAsync(capReadOnlyQueries?SqlRowLimit.Apply(batch.Sql,kind,limit):batch.Sql,limit,ct),null);}
            catch(OperationCanceledException){throw;}
            catch(Exception ex){outcome=new(number,batch.Line,[],ex.Message);}
            // Retain the first failure so later successful batches never hide an earlier error.
            outcomes.Add(outcome);if(outcomes.Count>100)outcomes.RemoveAt(outcomes[0].Error is null?0:1);
            progress?.Report(new(number,outcome.Error is null?"批次完成":$"第 {batch.Line} 行失败：{outcome.Error}"));
            if(outcome.Error is not null && !continueOnError)break;
        }
        return outcomes;
    }
}
