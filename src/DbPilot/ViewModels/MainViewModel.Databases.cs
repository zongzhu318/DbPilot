using DbPilot.Models;
using DbPilot.Providers;
using DbPilot.Services;

namespace DbPilot.ViewModels;
public sealed partial class MainViewModel
{
    private readonly Dictionary<Guid,string> _selectedDatabases=new();
    private readonly Dictionary<(Guid,string),ConnectionProfile> _usedProfiles=new();
    public bool IsClosed(ConnectionProfile profile)=>_disconnected.Contains(profile.Id);
    public bool IsOpen(ConnectionProfile profile)=>_connected.Contains(profile.Id);
    public ConnectionProfile DefaultProfile(ConnectionProfile profile)=>CloneForDatabase(profile,_selectedDatabases.GetValueOrDefault(profile.Id,profile.Database));
    public ConnectionProfile? CurrentProfile=>Connection is not {} p?null:Tab?.Connection?.Id==p.Id?Tab.Connection:CloneForDatabase(p,_selectedDatabases.GetValueOrDefault(p.Id,p.Database));
    private string ConnectionIconColor(ConnectionProfile p)=>!_connected.Contains(p.Id)?"#858B93":p.Kind switch{DatabaseKind.MySQL=>"#25B95A",DatabaseKind.PostgreSQL=>"#347AB5",DatabaseKind.SqlServer=>"#EC981B",_=>"#00A6A6"};
    private string ConnectionNodeLabel(ConnectionProfile p)=>$"{p.Name} · {(_connected.Contains(p.Id)?"已打开":"已关闭")}";
    private void UpdateConnectionNodes(ConnectionProfile profile,bool clear=false){
        foreach(var node in Nodes.SelectMany(n=>n.Children).Where(n=>n.Profile.Id==profile.Id)){
            node.Label=ConnectionNodeLabel(profile);node.IconColor=ConnectionIconColor(profile);
            if(clear){node.Loaded=false;node.Children.Clear();node.Children.Add(new(){Label="打开连接后加载数据库",Profile=profile,Loaded=true});}
        }
    }
    public async Task<IReadOnlyList<string>> ReadDatabasesAsync(ConnectionProfile profile,CancellationToken ct)
    {
        EnsureConnected(profile);await using var provider=_createProvider(profile);var names=await provider.DatabasesAsync(ct);ct.ThrowIfCancellationRequested();EnsureConnected(profile);Connected(profile);return names;
    }
    public void SelectDatabase(string database)
    {
        if(Busy)throw new InvalidOperationException("请等待当前任务完成后再切换数据库。");
        var profile=Connection??throw new InvalidOperationException("请选择连接。");EnsureConnected(profile);
        if(Tab is {} tab){EnsureNoPendingEdits(tab);tab.Connection=CloneForDatabase(profile,database);tab.Table=null;tab.Edits=null;tab.Result=null;tab.ResultSets=[];tab.BrowseSql="";tab.Page=0;tab.Filter="";ResultChanged?.Invoke(null);}
        _selectedDatabases[profile.Id]=database;Log("当前数据库 · "+database);
    }
    public Task<bool> ExecuteDdlAsync(ConnectionProfile profile,string sql)
    {
        return Run();
        async Task<bool> Run(){bool success=false;await Guard(async ct=>{
            EnsureConnected(profile);await using var provider=_createProvider(profile);
            var outcomes=await ScriptRunner.RunAsync(provider,new StringReader(sql),profile.Kind,false,1,null,ct);
            var error=outcomes.FirstOrDefault(o=>o.Error is not null);if(error is not null)throw new InvalidOperationException(error.Error);
            Connected(profile);success=true;Log("对象操作已完成。");
        });return success;}
    }
    public void EnsureObjectHasNoDrafts(ConnectionProfile profile,DbObject table)
    {
        foreach(var tab in Tabs.Where(t=>t.Connection?.Id==profile.Id&&t.Table is {} obj&&obj.Database==table.Database&&obj.Schema==table.Schema&&obj.Name==table.Name))EnsureNoPendingEdits(tab);
    }
    public void InvalidateObject(ConnectionProfile profile,DbObject table)
    {
        foreach(var tab in Tabs.Where(t=>t.Connection?.Id==profile.Id&&t.Table is {} obj&&obj.Database==table.Database&&obj.Schema==table.Schema&&obj.Name==table.Name)){tab.Table=null;tab.Edits=null;tab.Result=null;tab.ResultSets=[];tab.BrowseSql="";}
        ResultChanged?.Invoke(Tab?.Result);
    }
}
