using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DbStudio.Models;
using DbStudio.ViewModels;
using Renci.SshNet;

namespace DbStudio.Providers;
public sealed class SshDbProvider : IDbProvider,IEditableDbProvider
{
    private readonly ConnectionProfile _profile;
    private readonly IDbProvider _names;
    private readonly SemaphoreSlim _gate=new(1,1);
    private SshClient? _client;
    private ForwardedPortLocal? _port;
    private PrivateKeyFile? _key;
    private IDbProvider? _inner;
    public SshDbProvider(ConnectionProfile profile){_profile=MainViewModel.CloneForDatabase(profile,null);_names=DbProviderFactory.CreateDirect(_profile);}
    public static string Fingerprint(byte[] hostKey)=>"SHA256:"+Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
    private async Task<IDbProvider> Open(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try{
            if(_inner is not null)return _inner;
            var options=_profile.Ssh;
            if(string.IsNullOrWhiteSpace(options.Host)||string.IsNullOrWhiteSpace(options.Username))throw new InvalidOperationException("请填写 SSH 主机与用户名。");
            AuthenticationMethod auth;
            if(options.PrivateKeyPath.Length>0){_key=options.Passphrase.Length>0?new PrivateKeyFile(options.PrivateKeyPath,options.Passphrase):new PrivateKeyFile(options.PrivateKeyPath);auth=new PrivateKeyAuthenticationMethod(options.Username,_key);}
            else auth=new PasswordAuthenticationMethod(options.Username,options.Password);
            var info=new ConnectionInfo(options.Host,options.Port,options.Username,auth){Timeout=TimeSpan.FromSeconds(_profile.Timeout)};
            _client=new SshClient(info);string? presented=null;
            _client.HostKeyReceived+=(_,e)=>{presented=Fingerprint(e.HostKey);e.CanTrust=string.Equals(presented,options.HostKeyFingerprint.Trim(),StringComparison.Ordinal);};
            try{await _client.ConnectAsync(ct);}
            catch(Exception ex) when(presented is not null&&!string.Equals(presented,options.HostKeyFingerprint.Trim(),StringComparison.Ordinal)){
                throw new InvalidOperationException($"SSH 主机密钥未验证。服务器提供的指纹为 {presented}。请通过可信渠道核对后填入连接设置。",ex);
            }
            _port=new ForwardedPortLocal("127.0.0.1",0,_profile.Host,(uint)_profile.Port);_client.AddForwardedPort(_port);
            await Task.Run(()=>_port.Start(),ct);ct.ThrowIfCancellationRequested();
            var local=MainViewModel.CloneForDatabase(_profile,null);local.Host="127.0.0.1";local.Port=checked((int)_port.BoundPort);local.Ssh.Enabled=false;local.UsePooling=false;local.MinimumPoolSize=0;
            _inner=DbProviderFactory.CreateDirect(local);return _inner;
        }catch{Cleanup();throw;}
        finally{_gate.Release();}
    }
    private void Cleanup(){_port?.Dispose();_port=null;_client?.Dispose();_client=null;_key?.Dispose();_key=null;}
    public async Task TestAsync(CancellationToken ct)=>await(await Open(ct)).TestAsync(ct);
    public async Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken ct)=>await(await Open(ct)).DatabasesAsync(ct);
    public async Task<IReadOnlyList<DbObject>> ObjectsAsync(string database,CancellationToken ct)=>await(await Open(ct)).ObjectsAsync(database,ct);
    public async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(DbObject table,CancellationToken ct)=>await(await Open(ct)).ColumnsAsync(table,ct);
    public async Task<IReadOnlyList<IndexInfo>> IndexesAsync(DbObject table,CancellationToken ct)=>await(await Open(ct)).IndexesAsync(table,ct);
    public async Task<QueryResult> QueryAsync(string sql,int limit,CancellationToken ct)=>await(await Open(ct)).QueryAsync(sql,limit,ct);
    public async Task<IReadOnlyList<QueryResult>> QueryMultipleAsync(string sql,int limit,CancellationToken ct)=>await(await Open(ct)).QueryMultipleAsync(sql,limit,ct);
    public async Task<IReadOnlyList<QueryResult>> ExplainAsync(string sql,CancellationToken ct)=>await(await Open(ct)).ExplainAsync(sql,ct);
    public async IAsyncEnumerable<QueryResult> StreamAsync(string sql,int batchSize,[EnumeratorCancellation]CancellationToken ct){var provider=await Open(ct);await foreach(var batch in provider.StreamAsync(sql,batchSize,ct))yield return batch;}
    public async Task<string> DefinitionAsync(DbObject obj,CancellationToken ct)=>await(await Open(ct)).DefinitionAsync(obj,ct);
    public async Task<IReadOnlyList<ForeignKeyInfo>> ForeignKeysAsync(string database,CancellationToken ct)=>await(await Open(ct)).ForeignKeysAsync(database,ct);
    public async Task<long> InsertRowsAsync(DbObject table,IReadOnlyList<string> columns,IAsyncEnumerable<object?[]> rows,IProgress<TransferProgress>? progress,CancellationToken ct)=>await(await Open(ct)).InsertRowsAsync(table,columns,rows,progress,ct);
    public async Task<int> ApplyChangesAsync(DbObject table,IReadOnlyList<RowChange> changes,CancellationToken ct){var inner=await Open(ct);if(inner is not IEditableDbProvider editable)throw new NotSupportedException("当前连接不支持事务编辑。");return await editable.ApplyChangesAsync(table,changes,ct);}
    public string Quote(string name)=>_names.Quote(name);
    public string Qualified(DbObject table)=>_names.Qualified(table);
    public string PageSql(DbObject table,string filter,int page,int size)=>_names.PageSql(table,filter,page,size);
    public async ValueTask DisposeAsync(){try{if(_inner is not null)await _inner.DisposeAsync();}finally{Cleanup();await _names.DisposeAsync();_gate.Dispose();}}
}
