using Pn3DKit.Models;
using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>让实际批次协调器与变量保护共同运行，逐阶段检查关闭状态；宿主为离线替身。</summary>
public sealed partial class Pn3DKitTests
{
    /// <summary>打开、激活、QSAVE、导出、关闭和恢复原图全部在自动检查关闭期间执行。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportCheckerRemainsDisabledForEntireRealBatch(bool save)
    {
        using var fixture = new Fixture(); fixture.Add("dirty.dwg", modified: true); fixture.Add("clean.dwg");
        object? checker = 1; var operations = new List<string>();
        var scope = new ExportAutoCheckScope(() => checker, value => checker = value);
        var files = new RunLogFiles();
        fixture.Host.BeforeOperation = stage => { Assert.Equal(0, checker); operations.Add(stage); };
        scope.Disable();
        var result = await new BatchExporter().RunAsync(new(fixture.Project, fixture.Sources, null),
            fixture.Options(save), fixture.Host, _ => Assert.Equal(0, checker), files: files);
        Assert.Equal(0, checker);
        var outcome = scope.Finish(files.ProtectionReason);
        Assert.True(outcome.Restored); Assert.Equal(1, checker);
        Assert.Contains("Open", operations); Assert.Contains("Export", operations); Assert.Contains("Close", operations);
        Assert.Equal("Activate", operations[^1]); Assert.Same(fixture.Host.OriginalDrawing, fixture.Host.Active);
        Assert.Equal(save ? 1 : 0, fixture.Host.Saves);
        Assert.Equal(save ? new[] { "Exported", "Exported" } : new[] { "Skipped", "Exported" }, result.Items.Select(x => x.Status));
    }

    /// <summary>状态明确的逐图失败完成收尾后恢复原值，原有错误详情保持。</summary>
    [Theory]
    [InlineData("Open")]
    [InlineData("Save")]
    [InlineData("Export")]
    [InlineData("Close")]
    public async Task KnownBatchFailureRestoresCheckerAfterHostFinishes(string stage)
    {
        using var fixture = new Fixture(); fixture.Add("source.dwg", modified: true);
        object? checker = 1; var scope = new ExportAutoCheckScope(() => checker, value => checker = value);
        var failure = new CadOperationException("已知宿主错误 " + stage, true, true);
        switch (stage)
        {
            case "Open": fixture.Host.OpenFailure = failure; break;
            case "Save": fixture.Host.SaveFailure = failure; break;
            case "Export": fixture.Host.ExportFailure = failure; break;
            default: fixture.Host.CloseFailure = failure; break;
        }
        fixture.Host.BeforeOperation = _ => Assert.Equal(0, checker);
        scope.Disable();
        var result = await fixture.Run();
        Assert.Contains("已知宿主错误 " + stage, result.Items[0].Message);
        Assert.True(scope.Finish(null).Restored); Assert.Equal(1, checker);
    }

    /// <summary>选项阶段取消和开始前中断均恢复，不打开或保存源图。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreBatchCancellationRestoresWithoutCadOperations(bool atBatch)
    {
        using var fixture = new Fixture(); fixture.Add("source.dwg");
        object? checker = 1; var scope = new ExportAutoCheckScope(() => checker, value => checker = value);
        ExportAutoCheckOutcome? outcome = null;
        try
        {
            scope.Disable();
            if (!atBatch) return; // 对应选项提示取消，尚未调用批次协调器。
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(token: cancellation.Token));
        }
        finally
        {
            outcome = scope.Finish(null); Assert.True(outcome.Restored); Assert.Equal(1, checker);
            Assert.Empty(fixture.Host.Opened); Assert.Equal(0, fixture.Host.Saves);
        }
    }

    /// <summary>原生取消已确认结束时恢复；替身不把取消当作仍有命令运行。</summary>
    [Fact]
    public async Task KnownNativeCancellationRestoresChecker()
    {
        using var fixture = new Fixture(); fixture.Add("source.dwg"); fixture.Add("next.dwg");
        object? checker = 1; var scope = new ExportAutoCheckScope(() => checker, value => checker = value);
        fixture.Host.BeforeOperation = _ => Assert.Equal(0, checker);
        fixture.Host.ExportFailure = new CadOperationException("用户中断", true, true, true);
        scope.Disable(); var result = await fixture.Run();
        Assert.True(result.Canceled); Assert.Single(result.Items);
        Assert.True(scope.Finish(null).Restored); Assert.Equal(1, checker); Assert.Empty(fixture.Host.Closed);
    }

    /// <summary>沿用真实日志保护标记：未知原生失败停批且不恢复变量，已有报告不能被清理。</summary>
    [Fact]
    public async Task UnknownNativeFailureProtectsReportsAndDoesNotRestoreChecker()
    {
        using var fixture = new Fixture(); fixture.Add("source.dwg"); fixture.Add("next.dwg");
        object? checker = 1; var scope = new ExportAutoCheckScope(() => checker, value => checker = value);
        var files = new RunLogFiles();
        fixture.Host.BeforeOperation = stage =>
        {
            Assert.Equal(0, checker);
            if (stage == "Export") files.Protect("原生命令可能仍在运行"); // 与 AutoCadHost 的未知状态通道相同。
        };
        fixture.Host.ExportFailure = new CadOperationException("原生命令状态未知", false, true);
        scope.Disable();
        var result = await new BatchExporter().RunAsync(new(fixture.Project, fixture.Sources, null),
            fixture.Options(), fixture.Host, _ => { }, files: files);
        Assert.Single(result.Items); Assert.Empty(fixture.Host.Closed); Assert.NotNull(result.StopReason);
        var outcome = scope.Finish(files.ProtectionReason);
        Assert.True(outcome.HasIssue); Assert.False(outcome.Restored); Assert.Equal(0, checker);
        files.Seal(); var cleanup = files.Delete();
        Assert.Equal(0, cleanup.Deleted); Assert.NotEmpty(cleanup.Remaining); Assert.True(File.Exists(result.ReportPath));
    }
}
