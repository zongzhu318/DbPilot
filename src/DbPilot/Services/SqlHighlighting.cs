using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using DbPilot.Models;

namespace DbPilot.Services;
public static class SqlHighlighting
{
    public static IHighlightingDefinition Create(DatabaseKind kind,bool dark)
    {
        var words="SELECT FROM WHERE ORDER BY GROUP HAVING JOIN LEFT RIGHT INNER OUTER ON AS AND OR NOT NULL IS INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE INDEX LIMIT OFFSET FETCH NEXT ROWS ONLY DISTINCT UNION ALL DESC ASC CASE WHEN THEN ELSE END DROP ALTER PRIMARY KEY UNIQUE DEFAULT DATABASE SCHEMA EXPLAIN BEGIN COMMIT ROLLBACK TRUE FALSE IN EXISTS BETWEEN LIKE WITH OVER PARTITION COUNT SUM AVG MAX MIN";
        words+=kind switch{DatabaseKind.TDengine=>" STABLE TAGS INTERVAL SLIDING FILL SESSION STATE_WINDOW TBNAME TIMETRUNCATE TTL KEEP VGROUPS",DatabaseKind.SqlServer=>" TOP GO NVARCHAR IDENTITY MERGE OUTPUT",DatabaseKind.PostgreSQL=>" RETURNING SERIAL JSONB ILIKE CONFLICT",_=>" AUTO_INCREMENT ENGINE SHOW DESCRIBE REPLACE"};
        var keyword=dark?"#82B9F5":"#285EA7";var literal=dark?"#E3BE8B":"#956029";
        var comment=dark?"#83B8A9":"#56836D";var number=dark?"#C5A0E6":"#8753A4";
        var definition=$$"""
        <SyntaxDefinition name="SQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Keyword" foreground="{{keyword}}" fontWeight="bold" />
          <Color name="String" foreground="{{literal}}" />
          <Color name="Comment" foreground="{{comment}}" fontStyle="italic" />
          <Color name="Number" foreground="{{number}}" />
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="--" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
            <Span color="String" multiline="true" begin="'" end="'" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Span color="String" begin="`" end="`" />
            <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
            <Keywords color="Keyword">{{string.Concat(words.Split(' ',StringSplitOptions.RemoveEmptyEntries).Distinct().Select(w=>"<Word>"+w+"</Word>"))}}</Keywords>
          </RuleSet>
        </SyntaxDefinition>
        """;
        using var reader=XmlReader.Create(new StringReader(definition));return HighlightingLoader.Load(reader,HighlightingManager.Instance);
    }
}
