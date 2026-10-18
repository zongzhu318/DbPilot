using System.Data;
using DbStudio.Models;
using DbStudio.Providers;
using DbStudio.Services;

namespace DbStudio.Checks;

internal static class EditingChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        ColumnInfo[] schema=[new("id","integer",false,true,"","",IsGenerated:true),new("name","text",false,false,"",""),new("note","text",true,false,"'server-default'","")];
        QueryResult original=new(["id","name","note"],[new object?[]{1,"Alpha",null},new object?[]{2,"Beta","original"}],0,false,[typeof(int),typeof(string),typeof(string)]);
        var edits=new TableEditSession(original,schema);
        check(edits.PendingCount==0,"loaded table starts without staged changes");
        check(edits.Data.Columns[0].ReadOnly,"identity column is read-only");
        edits.Data.DefaultView.Sort="c1 DESC";
        edits.Data.DefaultView[0]["c1"]="Beta updated";
        var change=edits.GetChanges().Single();
        check((int)change.Original["id"]! == 2 && change.Values["name"]!.Equals("Beta updated"),"sorted edits use original primary key rather than visible row index");
        edits.Data.Rows[1][1]="Beta";
        check(edits.PendingCount==0,"restoring a value removes its pending update");
        var added=edits.AddRow();added[1]="New";
        check(!edits.GetChanges().Single().Values.ContainsKey("id") && !edits.GetChanges().Single().Values.ContainsKey("note"),"insert omits identity and untouched default fields");
        edits.SetNull(added,2);
        check(edits.GetChanges().Single().Values.ContainsKey("note") && edits.GetChanges().Single().Values["note"] is null,"explicit insert NULL differs from omitted server default");
        edits.DeleteRow(added);
        check(edits.PendingCount==0,"deleting a staged insert creates no database delete");
        edits.DeleteRow(edits.Data.Rows[0]);
        check(edits.GetChanges().Single().Kind==RowChangeKind.Delete && (int)edits.GetChanges().Single().Original["id"]! == 1,"delete retains original row snapshot");
        edits.Revert();
        check(edits.PendingCount==0 && edits.Data.Rows.Count==2 && edits.Data.Rows[0][1].Equals("Alpha"),"revert restores updates, deletes and inserts");
        bool noKey=false;try{_=new TableEditSession(original,schema.Select(c=>c with{PrimaryKey=false}).ToArray());}catch(InvalidOperationException){noKey=true;}
        check(noKey,"tables without primary keys cannot start edit sessions");
        var empty=new TableEditSession(new QueryResult(["id","name","note"],[],0,false,[typeof(int),typeof(string),typeof(string)]),schema);
        check(empty.Data.Columns[0].DataType==typeof(int),"empty-table editing preserves driver column types");
        var missing=empty.AddRow();bool required=false;try{empty.GetChanges();}catch(InvalidOperationException){required=true;}
        check(required,"inserts validate required non-generated fields");
        var defaultSchema=schema.Select(c=>c.Name=="name" ? c with{Default="",HasDefault=true}:c).ToArray();
        var defaults=new TableEditSession(new QueryResult(["id","name","note"],[],0,false,[typeof(int),typeof(string),typeof(string)]),defaultSchema);defaults.AddRow();
        check(defaults.GetChanges().Single().Values.Count==0,"empty-string server defaults are distinct from missing defaults");
        missing[1]="Valid";empty.CommitUncertain=true;bool retryBlocked=false;try{empty.GetChanges();}catch(InvalidOperationException){retryBlocked=true;}
        check(retryBlocked,"unknown commit outcome blocks resubmission");
        foreach(var kind in new[]{DatabaseKind.MySQL,DatabaseKind.PostgreSQL,DatabaseKind.SqlServer}){
            await using var provider=DbProviderFactory.Create(new ConnectionProfile{Kind=kind});
            var table=new DbObject("test","public","odd ]`\" table","表");
            edits.Data.Rows[0][1]="O'Reilly'; DROP TABLE users;--";
            var plans=EditCommandBuilder.Build(provider,kind,table,schema,edits.GetChanges());
            check(plans.Count==1 && !plans[0].Sql.Contains("O'Reilly") && plans[0].Parameters.Any(p=>Equals(p.Value,"O'Reilly'; DROP TABLE users;--")),kind+" edit values are parameterized");
            check(plans[0].Sql.Contains(" IS NULL") && plans[0].Parameters.Any(p=>Equals(p.Value,1)),kind+" concurrency predicate includes original key and NULL values");
            var inserted=EditCommandBuilder.Build(provider,kind,table,schema,[new(RowChangeKind.Insert,new Dictionary<string,object?>(),new Dictionary<string,object?>{{"name","safe"}})]).Single();
            check(inserted.Sql.StartsWith("INSERT INTO") && !inserted.Sql.Contains("safe"),kind+" insert uses quoted target and parameters");
            var compositeSchema=new[]{new ColumnInfo("tenant","int",false,true,"",""),new ColumnInfo("id","int",false,true,"",""),new ColumnInfo("label","text",true,false,"","")};
            var composite=new RowChange(RowChangeKind.Update,new Dictionary<string,object?>{{"tenant",5},{"id",8},{"label","old"}},new Dictionary<string,object?>{{"id",9},{"label","new"}});
            var compositePlan=EditCommandBuilder.Build(provider,kind,table,compositeSchema,[composite]).Single();
            check(compositePlan.Parameters.Any(p=>Equals(p.Value,5)) && compositePlan.Parameters.Any(p=>Equals(p.Value,8)) && compositePlan.Parameters.Any(p=>Equals(p.Value,9)),kind+" composite primary-key edits retain old and new keys");
            bool incomplete=false;try{EditCommandBuilder.Build(provider,kind,table,schema,[new(RowChangeKind.Delete,new Dictionary<string,object?>{{"id",1}},new Dictionary<string,object?>())]);}catch(InvalidOperationException){incomplete=true;}
            check(incomplete,kind+" incomplete snapshots are rejected");
            edits.Revert();
        }
        EditCommand[] commands=[new("UPDATE t SET x=@p0",[new("@p0","first")]),new("DELETE FROM t WHERE id=@p0",[new("@p0",7)])];
        await using(var db=new RecordingConnection()){
            int written=await RelationalBatchWriter.ApplyAsync(db,commands,CancellationToken.None);
            check(written==2 && db.Commits==1 && db.Rollbacks==0 && db.AllEnlisted,"all batch commands share one committed transaction");
            check(db.Executed[0].Values[0]!.Equals("first") && db.Executed[1].Values[0]!.Equals(7),"transaction writer binds original typed parameter values");
        }
        foreach(var affected in new[]{0,2}){
            await using var db=new RecordingConnection();db.Results.Enqueue(1);db.Results.Enqueue(affected);bool conflicted=false;
            try{await RelationalBatchWriter.ApplyAsync(db,commands,CancellationToken.None);}catch(DBConcurrencyException){conflicted=true;}
            check(conflicted && db.Rollbacks==1 && db.Commits==0,"affected count "+affected+" rolls back the entire batch");
        }
        await using(var db=new RecordingConnection{FailAt=1}){
            bool failed=false;try{await RelationalBatchWriter.ApplyAsync(db,commands,CancellationToken.None);}catch(InvalidOperationException){failed=true;}
            check(failed && db.Executed.Count==2 && db.Commits==0 && db.Rollbacks==1,"later command failure rolls back earlier commands");
        }
        await using(var db=new RecordingConnection()){
            using var cancellation=new CancellationTokenSource();db.AfterCommand=_=>cancellation.Cancel();bool cancelled=false;
            try{await RelationalBatchWriter.ApplyAsync(db,commands,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled && db.Executed.Count==1 && db.Rollbacks==1 && db.Commits==0,"cancellation after first command rolls back with an uncancelled token");
        }
        await using(var db=new RecordingConnection{FailCommit=true}){
            bool uncertain=false;try{await RelationalBatchWriter.ApplyAsync(db,commands,CancellationToken.None);}catch(EditCommitUncertainException){uncertain=true;}
            check(uncertain,"commit acknowledgement loss is reported as uncertain");
        }
        await using(var db=new RecordingConnection{FailAt=0,FailRollback=true}){
            bool uncertain=false;try{await RelationalBatchWriter.ApplyAsync(db,commands,CancellationToken.None);}catch(EditCommitUncertainException){uncertain=true;}
            check(uncertain,"rollback failure blocks unsafe retry");
        }
    }
}
