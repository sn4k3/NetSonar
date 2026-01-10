using System;
using NetSonar.Avalonia.Models;
using ZLinq;

namespace NetSonar.Avalonia.Settings;

public class CrashReports : RootCollectionFile<CrashReports, CrashReport>
{
    #region Constants
    public const int MaxCrashReports = 50;
    #endregion

    /// <inheritdoc />
    public override string DirectoryPath => App.LogsPath;

    /// <inheritdoc />
    public override string FileName => "crash_reports.json";

    /// <inheritdoc />
    protected override int DefaultDebounceSaveMilliseconds => 0;

    /// <inheritdoc />
    public override (int, CollectionSide)? TrimCollectionWhenExceeding =>
        new ValueTuple<int, CollectionSide>(MaxCrashReports, CollectionSide.Head);

    #region Methods

    public CrashReport? GetActual(long id)
    {
        return id == 0
            ? null
            : this.AsValueEnumerable().LastOrDefault(report => report.Id == id);
    }

    #endregion
}