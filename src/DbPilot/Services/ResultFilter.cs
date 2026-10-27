using System.Data;
using System.Globalization;
using System.Text;

namespace DbPilot.Services;

// Builds DataView row filters for in-result search; independent of the server-side WHERE filter.
public static class ResultFilter
{
    public static string Search(DataTable table,string? text)
    {
        if(table.Columns.Count==0)return "";
        var tokens=text?.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)??[];
        if(tokens.Length==0)return "";
        var clauses=tokens.Select(token=>"("+string.Join(" OR ",Enumerable.Range(0,table.Columns.Count).Select(i=>LikeClause(table,i,token)))+")");
        return string.Join(" AND ",clauses);
    }
    private static string LikeClause(DataTable table,int index,string text)
    {
        var column=table.Columns[index];
        var expression=column.DataType==typeof(string)?Name(column):"Convert("+Name(column)+",'System.String')";
        return expression+" LIKE '"+LikePattern(text)+"'";
    }
    private static string LikePattern(string text)
    {
        var pattern=new StringBuilder("%");
        foreach(var character in text){
            if(character=='\'')pattern.Append("''");
            else if(character is '[' or ']' or '%' or '*' or '?' or '_' or '#')pattern.Append('[').Append(character).Append(']');
            else pattern.Append(character);
        }
        return pattern.Append('%').ToString();
    }
    public static string? Exact(DataTable table,int index,object? value)
    {
        if(index<0||index>=table.Columns.Count)return null;
        var name=Name(table.Columns[index]);
        if(value is null or DBNull)return name+" IS NULL";
        if(value is byte[])return null;
        return value switch{
            string text=>name+" = '"+text.Replace("'","''")+"'",
            bool flag=>name+" = "+(flag?"true":"false"),
            DateTime date=>name+" = #"+date.ToString("yyyy-MM-dd HH:mm:ss.fffffff",CultureInfo.InvariantCulture)+"#",
            DateOnly date=>name+" = #"+date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"#",
            sbyte or byte or short or ushort or int or uint or long or ulong or decimal=>name+" = "+Convert.ToString(value,CultureInfo.InvariantCulture),
            float single when float.IsFinite(single)=>name+" = "+single.ToString("R",CultureInfo.InvariantCulture),
            double number when double.IsFinite(number)=>name+" = "+number.ToString("R",CultureInfo.InvariantCulture),
            DateTimeOffset offset=>AsText(name)+" = '"+offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz",CultureInfo.InvariantCulture).Replace("'","''")+"'",
            _=>AsText(name)+" = '"+System.Convert.ToString(value,CultureInfo.InvariantCulture)?.Replace("'","''")+"'"
        };
    }
    private static string Name(DataColumn column)=>"["+column.ColumnName.Replace("]","]]")+"]";
    private static string AsText(string name)=>"Convert("+name+",'System.String')";
}
