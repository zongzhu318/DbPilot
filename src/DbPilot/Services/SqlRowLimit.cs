using DbPilot.Models;

namespace DbPilot.Services;

// Caps read-only statements at execution time; the SQL shown in the editor is never rewritten.
public static class SqlRowLimit
{
    private static readonly HashSet<string> LimitWords = new(StringComparer.OrdinalIgnoreCase) { "LIMIT", "OFFSET", "FETCH", "TOP" };
    private static readonly HashSet<string> UnsafeWords = new(StringComparer.OrdinalIgnoreCase) { "INTO", "OUTFILE", "DUMPFILE", "FOR", "LOCK", "OPTION", "PROCEDURE", "COMPUTE" };
    private static readonly HashSet<string> WriteWords = new(StringComparer.OrdinalIgnoreCase) { "INSERT", "UPDATE", "DELETE", "MERGE" };

    public static string Apply(string sql, DatabaseKind kind, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(sql)) return sql;
        var scan = Scan(sql, kind);
        if (scan is not { ReadOnly: true, Limited: false, Unsafe: false, Multiple: false }) return sql;
        var rows = (long)limit + 1;
        var clause = kind switch
        {
            DatabaseKind.SqlServer => scan.HasOrderBy
                ? $" OFFSET 0 ROWS FETCH NEXT {rows} ROWS ONLY"
                : $" ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {rows} ROWS ONLY",
            _ => $" LIMIT {rows}"
        };
        return sql[..scan.InsertAt] + clause + sql[scan.InsertAt..];
    }

    private sealed record ScanResult(bool ReadOnly, bool Limited, bool Unsafe, bool Multiple, bool HasOrderBy, int InsertAt);

    private static ScanResult? Scan(string sql, DatabaseKind kind)
    {
        var depth = 0;
        var i = 0;
        var insertAt = -1;
        var pendingSemicolon = false;
        var multiple = false;
        var limited = false;
        var unsafeClause = false;
        var hasOrderBy = false;
        var orderPending = false;
        var sawSelect = false;
        string? firstWord = null;
        string? firstWrite = null;

        void Mark(int start, int end)
        {
            if (pendingSemicolon) multiple = true;
            pendingSemicolon = false;
            insertAt = end;
        }

        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (IsLineComment(sql, kind, i))
            {
                while (i < sql.Length && sql[i] is not ('\r' or '\n')) i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = SkipBlockComment(sql, i, kind is DatabaseKind.SqlServer or DatabaseKind.PostgreSQL);
                if (end < 0) return null;
                i = end;
                continue;
            }
            if (IsQuoted(sql, kind, ref i, out var closed))
            {
                if (!closed) return null;
                Mark(start, i);
                continue;
            }
            if (c == '(') { depth++; i++; Mark(start, i); continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); i++; Mark(start, i); continue; }
            if (c == ';')
            {
                i++;
                if (depth == 0)
                {
                    // Leading semicolons are empty statements; only a trailing run is safe to insert before.
                    if (firstWord is null) continue;
                    if (!pendingSemicolon) { insertAt = start; pendingSemicolon = true; }
                }
                else Mark(start, i);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '@' or '#')) i++;
                var word = sql[start..i];
                Mark(start, i);
                if (depth == 0)
                {
                    firstWord ??= word;
                    if (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) sawSelect = true;
                    else if (LimitWords.Contains(word)) limited = true;
                    else if (UnsafeWords.Contains(word)) unsafeClause = true;
                    else if (WriteWords.Contains(word)) firstWrite ??= word;
                    if (orderPending)
                    {
                        if (word.Equals("BY", StringComparison.OrdinalIgnoreCase)) hasOrderBy = true;
                        orderPending = false;
                    }
                    if (word.Equals("ORDER", StringComparison.OrdinalIgnoreCase)) orderPending = true;
                }
                continue;
            }
            i++;
            Mark(start, i);
        }

        if (firstWord is null || insertAt < 0) return null;
        var readOnly = firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase) && sawSelect && firstWrite is null;
        return new ScanResult(readOnly, limited, unsafeClause, multiple, hasOrderBy, insertAt);
    }

    private static bool IsLineComment(string sql, DatabaseKind kind, int i)
    {
        if (sql[i] == '#') return kind is DatabaseKind.MySQL or DatabaseKind.TDengine;
        if (sql[i] != '-' || i + 1 >= sql.Length || sql[i + 1] != '-') return false;
        if (kind is not (DatabaseKind.MySQL or DatabaseKind.TDengine)) return true;
        return i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]);
    }

    private static int SkipBlockComment(string sql, int i, bool nested)
    {
        var depth = 1;
        i += 2;
        while (i < sql.Length)
        {
            if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/')
            {
                if (--depth == 0) return i + 2;
                i += 2;
                continue;
            }
            if (nested && i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; continue; }
            i++;
        }
        return -1;
    }

    private static bool IsQuoted(string sql, DatabaseKind kind, ref int i, out bool closed)
    {
        closed = true;
        var c = sql[i];
        char close;
        var backslash = false;
        if (c == '\'')
        {
            close = '\'';
            if (kind is DatabaseKind.MySQL or DatabaseKind.TDengine) backslash = true;
            else if (kind == DatabaseKind.PostgreSQL && i > 0 && sql[i - 1] is 'E' or 'e'
                && (i < 2 || !(char.IsLetterOrDigit(sql[i - 2]) || sql[i - 2] is '_' or '$'))) backslash = true;
        }
        else if (c == '"')
        {
            close = '"';
            backslash = kind is DatabaseKind.MySQL;
        }
        else if (c == '`' && kind is DatabaseKind.MySQL or DatabaseKind.TDengine) close = '`';
        else if (c == '[' && kind == DatabaseKind.SqlServer) close = ']';
        else if (c == '$' && kind == DatabaseKind.PostgreSQL) return IsDollarQuoted(sql, ref i, out closed);
        else return false;

        i++;
        while (i < sql.Length)
        {
            if (backslash && sql[i] == '\\') { i += 2; continue; }
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                i++;
                return true;
            }
            i++;
        }
        closed = false;
        return true;
    }

    private static bool IsDollarQuoted(string sql, ref int i, out bool closed)
    {
        closed = true;
        var j = i + 1;
        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
        if (j >= sql.Length || sql[j] != '$') return false;
        var tag = sql[i..(j + 1)];
        var end = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            closed = false;
            i = sql.Length;
            return true;
        }
        i = end + tag.Length;
        return true;
    }
}
