using System.Data;
using DbPilot.Models;

namespace DbPilot.Services;

// One session per table tab. DataRow state belongs to the session, never to a sorted row index.
public sealed class TableEditSession
{
    private readonly QueryResult _original;
    private readonly Dictionary<DataRow,object?[]> _snapshots = [];
    private readonly HashSet<(DataRow Row,int Column)> _explicitNulls = [];
    public DataTable Data { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }
    public bool CommitUncertain { get; set; }
    public event Action? Changed;
    public int PendingCount => GetChanges(false).Count;
    public TableEditSession(QueryResult result,IReadOnlyList<ColumnInfo> columns)
    {
        if(!columns.Any(c=>c.PrimaryKey))throw new InvalidOperationException("该表没有主键，结果保持只读。");
        if(result.Columns.Count!=columns.Count || result.Columns.Distinct(StringComparer.Ordinal).Count()!=result.Columns.Count || !result.Columns.ToHashSet(StringComparer.Ordinal).SetEquals(columns.Select(c=>c.Name)))
            throw new InvalidOperationException("查询列与表结构不一致，结果保持只读。");
        _original=result;Columns=result.Columns.Select(n=>columns.Single(c=>c.Name==n)).ToArray();
        Data=ResultTable.Create(result);Data.AcceptChanges();
        for(int i=0;i<Data.Rows.Count;i++)_snapshots[Data.Rows[i]]=result.Rows[i].Select(CloneValue).ToArray();
        for(int i=0;i<Columns.Count;i++)Data.Columns[i].ReadOnly=!CanEditColumn(i);
        Data.ColumnChanged+=(_,e)=>{if(e.ProposedValue is null or DBNull)_explicitNulls.Add((e.Row,e.Column!.Ordinal));else _explicitNulls.Remove((e.Row,e.Column!.Ordinal));Changed?.Invoke();};
        Data.RowChanged+=(_,_)=>Changed?.Invoke();Data.RowDeleted+=(_,_)=>Changed?.Invoke();
    }
    private static object? CloneValue(object? value)=>value is byte[] b ? b.ToArray() : value;
    public bool CanEditColumn(int index)=>!Columns[index].IsGenerated && !_original.Rows.Any(r=>r[index] is Array) && !(_original.Types is {} types && types[index].IsArray);
    public DataRow AddRow()
    {
        EnsureWritable();var row=Data.NewRow();Data.Rows.Add(row);return row;
    }
    public void DeleteRow(DataRow row){EnsureWritable();row.Delete();}
    public void SetNull(DataRow row,int column)
    {
        EnsureWritable();if(!CanEditColumn(column))throw new InvalidOperationException("自动生成或二进制字段不可编辑。");
        if(!Columns[column].Nullable)throw new InvalidOperationException("该字段不允许 NULL。");
        _explicitNulls.Add((row,column));row[column]=DBNull.Value;
    }
    public void Revert()
    {
        EnsureWritable();foreach(DataRow row in Data.Rows)row.CancelEdit();Data.RejectChanges();_explicitNulls.Clear();Changed?.Invoke();
    }
    private void EnsureWritable(){if(CommitUncertain)throw new InvalidOperationException("上次提交结果未知，请核对数据库后重新加载，暂不能再次提交。");}
    public IReadOnlyList<RowChange> GetChanges(bool validate=true)
    {
        if(validate)EnsureWritable();var changes=new List<RowChange>();
        foreach(DataRow row in Data.Rows){
            if(row.RowState is DataRowState.Unchanged or DataRowState.Detached)continue;
            var original=new Dictionary<string,object?>(StringComparer.Ordinal);var values=new Dictionary<string,object?>(StringComparer.Ordinal);
            if(_snapshots.TryGetValue(row,out var snapshot))for(int i=0;i<Columns.Count;i++)original[Columns[i].Name]=CloneValue(snapshot[i]);
            if(row.RowState!=DataRowState.Deleted) for(int i=0;i<Columns.Count;i++){
                if(!CanEditColumn(i))continue;
                var value=row[i] is DBNull ? null : row[i];
                bool include=row.RowState==DataRowState.Added ? value is not null || _explicitNulls.Contains((row,i)) : !Equals(row[i,DataRowVersion.Original],row[i,DataRowVersion.Current]);
                if(include){if(validate && value is null && !Columns[i].Nullable)throw new InvalidOperationException($"字段 {Columns[i].Name} 不允许 NULL。");values[Columns[i].Name]=value;}
                if(validate && row.RowState==DataRowState.Added && !include && !Columns[i].Nullable && !Columns[i].HasDefault && string.IsNullOrEmpty(Columns[i].Default))throw new InvalidOperationException($"新行缺少必填字段：{Columns[i].Name}");
            }
            if(row.RowState==DataRowState.Modified && values.Count==0)continue;
            changes.Add(new(row.RowState switch{DataRowState.Added=>RowChangeKind.Insert,DataRowState.Deleted=>RowChangeKind.Delete,_=>RowChangeKind.Update},original,values));
        }
        return changes;
    }
    // Used only after a positively acknowledged commit; disables resubmission before refresh.
    public QueryResult AcceptCommitted()
    {
        foreach(DataRow row in Data.Rows.Cast<DataRow>().Where(r=>r.RowState!=DataRowState.Deleted))row.EndEdit();
        var rows=Data.Rows.Cast<DataRow>().Where(r=>r.RowState!=DataRowState.Deleted).Select(r=>Enumerable.Range(0,Columns.Count).Select(i=>!CanEditColumn(i) && _snapshots.TryGetValue(r,out var original) ? CloneValue(original[i]) : r[i] is DBNull ? null : r[i]).ToArray()).ToArray();
        Data.AcceptChanges();return new(_original.Columns,rows,0,_original.Truncated,_original.Types);
    }
}
