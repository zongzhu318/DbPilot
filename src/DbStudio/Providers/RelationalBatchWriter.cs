using System.Data;
using System.Data.Common;
using DbStudio.Models;
using Npgsql;
using NpgsqlTypes;

namespace DbStudio.Providers;

public static class RelationalBatchWriter
{
    public static async Task<int> ApplyAsync(DbConnection connection,IReadOnlyList<EditCommand> commands,CancellationToken ct)
    {
        if(commands.Count==0)return 0;
        var transaction=await connection.BeginTransactionAsync(ct);
        bool commitStarted=false;
        try{
            foreach(var plan in commands){
                ct.ThrowIfCancellationRequested();await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=plan.Sql;command.CommandTimeout=30;
                foreach(var value in plan.Parameters){var parameter=command.CreateParameter();parameter.ParameterName=value.Name;parameter.Value=value.Value??DBNull.Value;
                    // Let PostgreSQL infer destination types for JSON, enums and other string-backed types.
                    if(parameter is NpgsqlParameter np && value.Value is string)np.NpgsqlDbType=NpgsqlDbType.Unknown;
                    command.Parameters.Add(parameter);
                }
                if(await command.ExecuteNonQueryAsync(ct)!=1)throw new DBConcurrencyException("数据已被其他操作修改或删除，本次全部变更将回滚。请先保留改动并刷新数据。");
            }
            ct.ThrowIfCancellationRequested();commitStarted=true;
            // Cancellation is allowed before this boundary; never interrupt COMMIT acknowledgement.
            await transaction.CommitAsync(CancellationToken.None);return commands.Count;
        }catch(Exception ex){
            bool rollbackFailed=false;try{await transaction.RollbackAsync(CancellationToken.None);}catch{rollbackFailed=true;}
            if(commitStarted || rollbackFailed)throw new EditCommitUncertainException("事务结果无法确认，请核对数据库后重新加载；不要重复提交。",ex);
            throw;
        }
        finally{
            // Cleanup failure must not turn a positively acknowledged commit into a retryable batch.
            // The owning provider still closes the connection; rollback failures are handled above.
            try{await transaction.DisposeAsync();}catch{ }
        }
    }
}
