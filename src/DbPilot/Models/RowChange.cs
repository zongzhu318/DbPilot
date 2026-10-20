namespace DbPilot.Models;

public enum RowChangeKind { Insert, Update, Delete }
public sealed record RowChange(RowChangeKind Kind, IReadOnlyDictionary<string,object?> Original, IReadOnlyDictionary<string,object?> Values);
public sealed record EditParameter(string Name,object? Value);
public sealed record EditCommand(string Sql,IReadOnlyList<EditParameter> Parameters);

public sealed class EditCommitUncertainException(string message,Exception inner) : Exception(message,inner);
