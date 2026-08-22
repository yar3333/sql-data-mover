using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

public enum CopyStage
{
    Preparing,
    LoadingTargetMap,
    Copying,
    Completed,
    Failed
}

/// <summary>Сообщение о прогрессе копирования.</summary>
public sealed record CopyProgress(DbObjectName Table, CopyStage Stage, long ProcessedRows, string Message, bool IsError = false);
