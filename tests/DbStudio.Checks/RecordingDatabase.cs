using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbStudio.Checks;

// Protocol double: records transaction enlistment and failure boundaries without a live server.
internal sealed class RecordingConnection : DbConnection
{
    public List<(string Sql,object?[] Values)> Executed { get; }=[];
    public Queue<int> Results { get; }=new();
    public int FailAt { get; set; }=-1;
    public bool FailCommit { get; set; }
    public bool FailRollback { get; set; }
    public int Commits { get; set; }
    public int Rollbacks { get; set; }
    public bool AllEnlisted { get; set; }=true;
    public Action<int>? AfterCommand { get; set; }
    public RecordingTransaction? Transaction { get; private set; }
    [AllowNull] public override string ConnectionString { get; set; }="";
    public override string Database=>"checks";
    public override string DataSource=>"checks";
    public override string ServerVersion=>"1";
    public override ConnectionState State=>ConnectionState.Open;
    public override void ChangeDatabase(string databaseName){}
    public override void Close(){}
    public override void Open(){}
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)=>Transaction=new RecordingTransaction(this,isolationLevel);
    protected override DbCommand CreateDbCommand()=>new RecordingCommand(this);
    public int Execute(RecordingCommand command)
    {
        AllEnlisted&=ReferenceEquals(command.Transaction,Transaction);
        var index=Executed.Count;
        Executed.Add((command.CommandText,command.Parameters.Cast<DbParameter>().Select(p=>p.Value).ToArray()));
        if(index==FailAt)throw new InvalidOperationException("simulated constraint error");
        AfterCommand?.Invoke(index);return Results.Count>0 ? Results.Dequeue() : 1;
    }
}
internal sealed class RecordingTransaction(RecordingConnection owner,IsolationLevel isolation) : DbTransaction
{
    public override IsolationLevel IsolationLevel=>isolation;
    protected override DbConnection DbConnection=>owner;
    public override void Commit(){owner.Commits++;if(owner.FailCommit)throw new IOException("commit acknowledgement lost");}
    public override void Rollback(){owner.Rollbacks++;if(owner.FailRollback)throw new IOException("rollback acknowledgement lost");}
}
internal sealed class RecordingCommand(RecordingConnection owner) : DbCommand
{
    [AllowNull] public override string CommandText { get; set; }="";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }=owner;
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbParameterCollection DbParameterCollection { get; }=new RecordingParameters();
    public override void Cancel(){}
    public override int ExecuteNonQuery()=>owner.Execute(this);
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(ExecuteNonQuery());}
    public override object? ExecuteScalar()=>throw new NotSupportedException();
    public override void Prepare(){}
    protected override DbParameter CreateDbParameter()=>new RecordingParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)=>throw new NotSupportedException();
}
internal sealed class RecordingParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }=ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    [AllowNull] public override string ParameterName { get; set; }="";
    [AllowNull] public override string SourceColumn { get; set; }="";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType(){}
}
internal sealed class RecordingParameters : DbParameterCollection
{
    private readonly List<DbParameter> _items=[];
    public override int Count=>_items.Count;
    public override object SyncRoot=>this;
    public override int Add(object value){_items.Add((DbParameter)value);return _items.Count-1;}
    public override void AddRange(Array values){foreach(var value in values)Add(value!);}
    public override void Clear()=>_items.Clear();
    public override bool Contains(object value)=>_items.Contains((DbParameter)value);
    public override bool Contains(string value)=>IndexOf(value)>=0;
    public override void CopyTo(Array array,int index)=>((ICollection)_items).CopyTo(array,index);
    public override IEnumerator GetEnumerator()=>_items.GetEnumerator();
    public override int IndexOf(object value)=>_items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName)=>_items.FindIndex(p=>p.ParameterName==parameterName);
    public override void Insert(int index,object value)=>_items.Insert(index,(DbParameter)value);
    public override void Remove(object value)=>_items.Remove((DbParameter)value);
    public override void RemoveAt(int index)=>_items.RemoveAt(index);
    public override void RemoveAt(string parameterName)=>RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index)=>_items[index];
    protected override DbParameter GetParameter(string parameterName)=>_items[IndexOf(parameterName)];
    protected override void SetParameter(int index,DbParameter value)=>_items[index]=value;
    protected override void SetParameter(string parameterName,DbParameter value){int index=IndexOf(parameterName);if(index<0)_items.Add(value);else _items[index]=value;}
}
