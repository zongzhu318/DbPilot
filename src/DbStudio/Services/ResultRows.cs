using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using DbStudio.Models;

namespace DbStudio.Services;

// Exposes one unambiguous integer indexer to Avalonia (DataRowView has overloaded indexers).
public sealed class ResultRow(DataRowView view) : Observable,IEditableObject
{
    public DataRow Row=>view.Row;
    public object? this[int index]
    {
        get=>view[index] is DBNull ? null : view[index];
        set{view[index]=value??DBNull.Value;Refresh();}
    }
    internal void Refresh()=>Changed("Item[]");
    public void BeginEdit()=>view.BeginEdit();
    public void EndEdit()=>view.EndEdit();
    public void CancelEdit()=>view.CancelEdit();
}

public sealed class ResultRows : ObservableCollection<ResultRow>,IDisposable
{
    private readonly DataView _view;
    public DataTable Table=>_view.Table!;
    public ResultRows(DataView view){_view=view;Reconcile();view.ListChanged+=ViewChanged;}
    private void ViewChanged(object? sender,ListChangedEventArgs e)
    {
        if(e.ListChangedType==ListChangedType.ItemChanged){foreach(var row in this)row.Refresh();}
        else Reconcile();
    }
    private void Reconcile()
    {
        var cache=this.ToDictionary(r=>r.Row);
        var desired=_view.Cast<DataRowView>().Select(v=>cache.TryGetValue(v.Row,out var row)?row:new ResultRow(v)).ToArray();
        for(int i=0;i<desired.Length;i++){
            if(i<Count && ReferenceEquals(this[i],desired[i]))continue;
            int previous=IndexOf(desired[i]);if(previous>=0)Move(previous,i);else Insert(i,desired[i]);
        }
        while(Count>desired.Length)RemoveAt(Count-1);
        foreach(var row in this)row.Refresh();
    }
    public void Dispose()=>_view.ListChanged-=ViewChanged;
}
