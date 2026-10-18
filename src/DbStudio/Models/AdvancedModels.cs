namespace DbStudio.Models;

public sealed class SshOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string HostKeyFingerprint { get; set; } = "";
}
public sealed record ForeignKeyInfo(string Name,string Schema,string Table,string Column,string ReferencedSchema,string ReferencedTable,string ReferencedColumn,string OnDelete="NO ACTION",string OnUpdate="NO ACTION",bool Deferrable=false,bool InitiallyDeferred=false);
public sealed record ScriptBatch(string Sql,int Line);
public sealed record ScriptOutcome(int Batch,int Line,IReadOnlyList<QueryResult> Results,string? Error);
public sealed record TransferProgress(long Rows,string Stage);
public sealed record SqlTemplate(string Name,string Sql);
public sealed class FieldDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "VARCHAR(255)";
    public bool Nullable { get; set; } = true;
    public bool PrimaryKey { get; set; }
    public bool Identity { get; set; }
    public bool Tag { get; set; }
    public string Default { get; set; } = "";
}
public sealed record SchemaDifference(string Kind,string Object,string Detail,string Sql,bool Destructive);
