using System.Data;
using DbStudio.Models;

namespace DbStudio.Services;
public static class ResultTable
{
    public static DataTable Create(QueryResult result)
    {
        var table=new DataTable();
        for(int i=0;i<result.Columns.Count;i++){
            var types=result.Rows.Select(r=>r[i]).Where(v=>v is not null and not DBNull).Select(v=>v!.GetType()).Distinct().ToArray();
            var sourceType=result.Types is { } schema && i<schema.Count ? schema[i] : types.Length==1 ? types[0] : typeof(string);
            var type=sourceType.IsArray ? typeof(string) : sourceType;
            var column=table.Columns.Add("c"+i,type);
            if(type==typeof(DateTime)) column.DateTimeMode=result.Rows.Select(r=>r[i]).OfType<DateTime>().FirstOrDefault().Kind==DateTimeKind.Utc ? DataSetDateTime.Utc : DataSetDateTime.Unspecified;
        }
        foreach(var row in result.Rows){
            var values=new object[row.Length];
            for(int i=0;i<row.Length;i++) values[i]=row[i] is null or DBNull ? DBNull.Value : table.Columns[i].DataType==typeof(string) ? SqlTools.Display(row[i]) : row[i]!;
            table.Rows.Add(values);
        }
        return table;
    }
}
