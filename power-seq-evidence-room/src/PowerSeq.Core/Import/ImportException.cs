namespace PowerSeq.Core.Import;

/// <summary>明确的格式错误：定位到行列，不静默丢弃。</summary>
public sealed class ImportFormatException : Exception
{
    public int Line { get; }
    public int? Column { get; }

    public ImportFormatException(string message, int line, int? column = null)
        : base(column is null ? $"第 {line} 行: {message}" : $"第 {line} 行第 {column} 列: {message}")
    {
        Line = line;
        Column = column;
    }
}

public sealed record ImportDiagnostic(string Severity, int? Line, string Message);
