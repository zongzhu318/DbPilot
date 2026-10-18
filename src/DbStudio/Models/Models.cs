using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace DbStudio.Models;

public enum DatabaseKind { MySQL, PostgreSQL, SqlServer, TDengine }
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新建连接";
    public string Group { get; set; } = "本地开发";
    public DatabaseKind Kind { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public bool Ssl { get; set; }
    public bool AllowPublicKeyRetrieval { get; set; }
    public bool IntegratedSecurity { get; set; }
    public int Timeout { get; set; } = 15;
    public string Timezone { get; set; } = "Asia/Shanghai";
    public SshOptions Ssh { get; set; } = new();
    public int MinimumPoolSize { get; set; }
    public int MaximumPoolSize { get; set; } = 100;
    public bool UsePooling { get; set; } = true;
    public override string ToString() => $"{Name} · {Kind}";
}
public sealed record DbObject(string Database, string Schema, string Name, string Kind,string Signature="",string Parent="")
{
    public override string ToString() => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}
public sealed record ColumnInfo(string Name, string Type, bool Nullable, bool PrimaryKey, string Default, string Comment, bool IsTag = false, bool IsGenerated = false, bool HasDefault = false, bool IsIdentity = false, string GenerationExpression = "", bool Stored = false);
public sealed record IndexInfo(string Name, string Columns, string Kind);
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, long Affected, bool Truncated, IReadOnlyList<Type>? Types = null);
public sealed record QueryHistoryEntry(DateTimeOffset ExecutedAt,Guid ConnectionId,string ConnectionName,string Sql,long Elapsed,int Rows,string Database="")
{
    public override string ToString()=>$"{ExecutedAt.ToLocalTime():MM-dd HH:mm}  {ConnectionName} · {Elapsed} ms · {Rows} 行";
}
public sealed class QueryTab : Observable
{
    private string _sql = "-- 在这里编写 SQL，按 Ctrl+Enter 执行\nSELECT 1;";
    public string SavedSql { get; set; } = "-- 在这里编写 SQL，按 Ctrl+Enter 执行\nSELECT 1;";
    public bool IsDirty => Sql != SavedSql;
    private string _title="查询";
    public string Title { get=>_title; set=>Set(ref _title,value); }
    public string Sql { get => _sql; set => Set(ref _sql, value); }
    public string? FilePath { get; set; }
    public ConnectionProfile? Connection { get; set; }
    public DbObject? Table { get; set; }
    public int Page { get; set; }
    public QueryResult? Result { get; set; }
    public IReadOnlyList<QueryResult> ResultSets { get; set; } = [];
    public int ResultIndex { get; set; }
    public Services.TableEditSession? Edits { get; set; }
    public string BrowseSql { get; set; } = "";
    public string Filter { get; set; } = "";
    public int PageSize { get; set; } = 100;
    public override string ToString() => Title;
}
public sealed class ObjectNode : Observable
{
    public bool IsGroup { get; init; }
    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible,value); }
    private string _label="";
    public string Label { get=>_label; set=>Set(ref _label,value); }
    public ConnectionProfile Profile { get; init; } = null!;
    public DbObject? Object { get; init; }
    public string? Database { get; init; }
    public ObservableCollection<ObjectNode> Children { get; } = [];
    public bool Loaded { get; set; }
    public override string ToString() => Label;
}
public sealed class AppSettings
{
    public bool Dark { get; set; }
    public double FontSize { get; set; } = 14;
    public bool WordWrap { get; set; }
    public bool LineNumbers { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    public double Width { get; set; } = 1360;
    public double Height { get; set; } = 860;
    public string FontFamily { get; set; } = "Cascadia Code,Consolas,monospace";
    public string ExecuteGesture { get; set; } = "Ctrl+Enter";
    public string CompletionGesture { get; set; } = "Ctrl+Space";
    public bool PersistLogs { get; set; } = true;
    public bool ContinueOnScriptError { get; set; }
    public double EditorRatio { get; set; } = 2.0/3;
}
