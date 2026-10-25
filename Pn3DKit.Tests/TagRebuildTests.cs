using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>离线验证重建的保守冲突策略、写入生命周期、取消及追加日志；不加载 Autodesk。</summary>
public sealed class TagRebuildTests
{
    /// <summary>创建一个带有原始位号和重建结果的项目数据行。</summary>
    private static TagRebuildCandidate Item(string key = "PnId:1", string old = "PT-101", string next = "PT-102",
        string part = "PnId", TagCheckStatus status = TagCheckStatus.Mismatch, string? exclusion = null,
        string registry = "", string candidate = "") =>
        new(key, new(new(part, "Instrument", key.Split(':')[^1], "#(TargetObject.Number)", old),
            next, status, [new("TargetObject.Number", old, next)], status == TagCheckStatus.Error ? "SDK ErrorStatus=KeyNotFound" : null),
            registry, candidate, exclusion);

    /// <summary>获取全都空闲的预检。</summary>
    private static IReadOnlyList<TagRebuildPlan> Free(params TagRebuildCandidate[] items) =>
        TagRebuildPolicy.Prepare(items, _ => []);

    /// <summary>确认及保存均必须明确为 Y；默认和任意其他输入都不写。</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("N", false)]
    [InlineData("No", false)]
    [InlineData("Y", true)]
    [InlineData("yes", true)]
    [InlineData("y", true)]
    [InlineData("其他", false)]
    public void ConfirmationDefaultsToNo(string? input, bool expected) =>
        Assert.Equal(expected, TagRebuildPolicy.IsYes(input));

    /// <summary>六种检查状态被分类为一致、可重建或不可重建。</summary>
    [Theory]
    [InlineData(TagCheckStatus.Consistent, TagRebuildState.Consistent)]
    [InlineData(TagCheckStatus.Mismatch, TagRebuildState.Ready)]
    [InlineData(TagCheckStatus.NoFormat, TagRebuildState.Unavailable)]
    [InlineData(TagCheckStatus.NoTag, TagRebuildState.Unavailable)]
    [InlineData(TagCheckStatus.TemporaryTag, TagRebuildState.Unavailable)]
    [InlineData(TagCheckStatus.Error, TagRebuildState.Unavailable)]
    public void AllCheckStatesAreClassified(TagCheckStatus check, TagRebuildState expected) =>
        Assert.Equal(expected, Assert.Single(Free(Item(status: check))).State);

    /// <summary>空期望值或任意位置含问号均不可写。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("?PT-102")]
    [InlineData("PT-?102")]
    public void IncompleteExpectedTagIsExcluded(string next) =>
        Assert.Equal(TagRebuildState.Unavailable, Assert.Single(Free(Item(next: next))).State);

    /// <summary>Piping 及 SDK 已判定的 PipeLines 子类不执行写入。</summary>
    [Fact]
    public void ScopeAndSdkErrorsRemainExplicit()
    {
        Assert.Contains("管线组", Assert.Single(Free(Item(part: "Piping"))).Reason);
        Assert.Contains("PipeLines", Assert.Single(Free(Item(exclusion: "PipeLines 及其子类别由管线组决定"))).Reason);
        Assert.Contains("ErrorStatus=KeyNotFound", Assert.Single(Free(Item(status: TagCheckStatus.Error))).Reason);
    }

    /// <summary>空闲目标可重建，但预检不得调用任何写入接口。</summary>
    [Fact]
    public void FreeTargetIsReady()
    {
        var plan = Assert.Single(Free(Item()));
        Assert.Equal(TagRebuildState.Ready, plan.State);
        Assert.Equal(TagRebuildAction.Pending, TagRebuildPolicy.Preview(plan).Action);
    }

    /// <summary>本批内相同目标都标记冲突，大小写差异也保守跳过。</summary>
    [Theory]
    [InlineData("PT-102")]
    [InlineData("pt-102")]
    public void DuplicateBatchTargetsAreAllConflicts(string second) =>
        Assert.All(Free(Item(), Item("PnId:2", "PT-103", second)), x => Assert.Equal(TagRebuildState.Conflict, x.State));

    /// <summary>注册表中的内部键决定同表冲突，避免把显示 Tag 当成注册键。</summary>
    [Fact]
    public void CanonicalRegistryKeysDriveDuplicates()
    {
        var a = Item(registry: "R", candidate: "internal:key");
        var b = Item("PnId:2", "PT-103", "PT-104", registry: "R", candidate: "internal:key");
        Assert.All(Free(a, b), x => Assert.Equal(TagRebuildState.Conflict, x.State));
        Assert.All(Free(a, b with { Registry = "other" }), x => Assert.Equal(TagRebuildState.Ready, x.State));
    }

    /// <summary>一个注册表缺失时仍以显示 Tag 保守检测同批重复。</summary>
    [Fact]
    public void MissingRegistryStillDetectsBatchDuplicates() =>
        Assert.All(Free(Item(), Item("PnId:2", "PT-103", registry: "R", candidate: "internal:key")),
            x => Assert.Equal(TagRebuildState.Conflict, x.State));

    /// <summary>项目其他图纸的占用者成为证据，不被计作本批目标。</summary>
    [Fact]
    public void ExternalOccupantIsEvidenceOnly()
    {
        var plans = TagRebuildPolicy.Prepare([Item()], _ => ["PnId:999"]);
        Assert.Single(plans);
        Assert.Equal(TagRebuildState.Conflict, plans[0].State);
        Assert.Contains("PnId:999", plans[0].Reason);
    }

    /// <summary>同一行的两个图形只写一次，日志保留全部句柄。</summary>
    [Fact]
    public void RowAliasesAreMergedBeforeConflictAndWrite()
    {
        var first = Item();
        var alias = first with { Check = first.Check with { Subject = first.Check.Subject with { Handle = "A2" } } };
        var plans = Free(first, alias);
        Assert.Equal("1、A2", Assert.Single(plans).Item.Check.Subject.Handle);
        var host = new Host();
        var result = TagRebuildBatch.Run(plans, host, new Journal(), (_, _) => { });
        Assert.Single(result.Records);
        Assert.Single(host.Events, x => x.StartsWith("write:"));
        Assert.Equal(TagRebuildState.Unavailable, Assert.Single(Free(first, alias with
            { Check = alias.Check with { ExpectedTag = "CHANGED" } })).State);
    }

    /// <summary>确认后新发现的占用传播到原来一致的参与者，但不扩大用户同意的候选范围。</summary>
    [Fact]
    public void RefreshPreservesConsentAndAddsAllConflictParticipants()
    {
        var a = Item();
        var b = Item("PnId:2", "PT-103", "PT-103", status: TagCheckStatus.Consistent);
        var preview = Free(a, b);
        var refreshed = TagRebuildPolicy.Prepare([a, b], _ => [b.Key]);
        Assert.All(TagRebuildPolicy.RestrictToPreview(preview, refreshed), x => Assert.Equal(TagRebuildState.Conflict, x.State));
        var previouslySkipped = Free(a with { Exclusion = "不可写" });
        Assert.Equal(TagRebuildState.Unavailable,
            Assert.Single(TagRebuildPolicy.RestrictToPreview(previouslySkipped, Free(a))).State);
    }

    /// <summary>每 50 个进度也包含写入前跳过项，取消发生在对象间时保留已更新结果。</summary>
    [Fact]
    public void ProgressIncludesSkippedObjectsAndCancellationBetweenObjects()
    {
        var updates = new List<int>();
        var items = Enumerable.Range(1, 100).Select(n => Item("PnId:" + n, next: "PT-" + (1000 + n))).ToArray();
        TagRebuildBatch.Run(Free(items), new Host { Stale = true }, new Journal(), (done, _) => updates.Add(done));
        Assert.Contains(50, updates);
        Assert.Contains(100, updates);
        var host = new Host { Context = n => { if (n == 4) throw new OperationCanceledException("Esc"); } };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, new Journal(), (_, _) => { });
        Assert.Equal(new[] { TagRebuildAction.Updated, TagRebuildAction.Unexecuted }, result.Records.Select(x => x.Action));
        Assert.True(result.SafeToSave);
        Assert.Equal("regen", host.Events[^1]);
    }

    /// <summary>自身数据行不形成冲突。</summary>
    [Fact]
    public void OwnRowDoesNotConflict() =>
        Assert.Equal(TagRebuildState.Ready, Assert.Single(TagRebuildPolicy.Prepare([Item()], x => [x.Key])).State);

    /// <summary>互换和占用链全部参与者都跳过，不尝试先释放后写入。</summary>
    [Theory]
    [InlineData("PT-101")]
    [InlineData("PT-103")]
    public void SwapsAndChainsSkipBothEnds(string secondTarget)
    {
        var a = Item();
        var b = Item("PnId:2", "PT-102", secondTarget);
        var plans = TagRebuildPolicy.Prepare([a, b], x => x.Key == a.Key ? [b.Key]
            : secondTarget == a.Check.Subject.ExistingTag ? [a.Key] : []);
        Assert.All(plans, x => Assert.Equal(TagRebuildState.Conflict, x.State));
    }

    /// <summary>即使占用端本身位号一致，也记录为涉及的冲突对象。</summary>
    [Fact]
    public void ConsistentSelectedOccupantIsIncluded()
    {
        var a = Item();
        var b = Item("PnId:2", "PT-102", "PT-102", status: TagCheckStatus.Consistent);
        Assert.All(TagRebuildPolicy.Prepare([a, b], _ => [b.Key]),
            x => Assert.Equal(TagRebuildState.Conflict, x.State));
    }

    /// <summary>未知注册表错误不可当成空闲或静默回退。</summary>
    [Fact]
    public void RegistryErrorExcludesTarget()
    {
        var plan = Assert.Single(TagRebuildPolicy.Prepare([Item()], _ =>
            throw new InvalidOperationException("SDK DataLinks ErrorCode=FailedToLoadCache")));
        Assert.Equal(TagRebuildState.Unavailable, plan.State);
        Assert.Contains("FailedToLoadCache", plan.Reason);
    }

    /// <summary>预检的图纸／项目失效必须立即停止，不转换为对象错误后继续查询。</summary>
    [Fact]
    public void PreflightContextFailureStopsImmediately()
    {
        int queries = 0;
        Assert.Throws<InvalidOperationException>(() => TagRebuildPolicy.Prepare(
            [Item(), Item("PnId:2", next: "PT-104")], _ => { queries++; return []; },
            () => throw new InvalidOperationException("原图纸已关闭")));
        Assert.Equal(0, queries);
    }

    /// <summary>预检取消向上层传播，不误判为普通 SDK 错误。</summary>
    [Fact]
    public void PreflightCancellationPropagates() =>
        Assert.Throws<OperationCanceledException>(() =>
            TagRebuildPolicy.Prepare([Item()], _ => throw new OperationCanceledException()));

    /// <summary>明确不确认后为候选追加未执行，保留其他预检动作。</summary>
    [Fact]
    public void DecliningWritesNoProperties()
    {
        var log = new Journal();
        var plans = Free(Item(), Item("PnId:2", status: TagCheckStatus.NoTag));
        foreach (var plan in plans) log.Append(TagRebuildPolicy.Preview(plan));
        var final = TagRebuildPolicy.Decline(plans, log, "用户未确认");
        Assert.Equal(TagRebuildAction.Unexecuted, final[0].Action);
        Assert.Equal(TagRebuildAction.Skipped, final[1].Action);
        Assert.Equal(3, log.Records.Count);
    }

    /// <summary>成功写入仅建立一次撤销组，关闭后刷新；回读严格匹配。</summary>
    [Fact]
    public void SuccessfulBatchUsesOneUndoGroup()
    {
        var host = new Host();
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", "PT-103", "PT-104")), host, new Journal(), (_, _) => { });
        Assert.All(result.Records, x => Assert.Equal(TagRebuildAction.Updated, x.Action));
        Assert.Equal(new[] { "begin", "write:PnId:1", "write:PnId:2", "end", "regen" }, host.Events);
        Assert.True(result.SafeToSave);
        Assert.True(result.Attempted);
    }

    /// <summary>撤销已关闭或 BEGIN 失败时零写入，也不会结束别人的组。</summary>
    [Fact]
    public void BeginFailureNeverWritesOrEndsOtherGroup()
    {
        var host = new Host { BeginFailure = true };
        var result = TagRebuildBatch.Run(Free(Item()), host, new Journal(), (_, _) => { });
        Assert.Empty(host.Events);
        Assert.False(result.Attempted);
        Assert.False(result.SafeToSave);
        Assert.Equal(TagRebuildAction.Unexecuted, Assert.Single(result.Records).Action);
    }

    /// <summary>只读或未以读写方式登记的前置失败均阻止 BEGIN 和一切写入。</summary>
    [Theory]
    [InlineData("图纸只读")]
    [InlineData("未以读写方式加入项目")]
    public void WritePermissionFailureHasNoMutation(string reason)
    {
        var host = new Host { Context = _ => throw new InvalidOperationException(reason) };
        var result = TagRebuildBatch.Run(Free(Item()), host, new Journal(), (_, _) => { });
        Assert.Empty(host.Events);
        Assert.False(result.Attempted);
        Assert.False(result.SafeToSave);
        Assert.Contains(reason, result.StopReason);
    }

    /// <summary>全是跳过项时不创建空撤销组。</summary>
    [Fact]
    public void NoCandidatesNeedsNoUndoGroup()
    {
        var host = new Host();
        TagRebuildBatch.Run(Free(Item(status: TagCheckStatus.NoTag)), host, new Journal(), (_, _) => { });
        Assert.Empty(host.Events);
    }

    /// <summary>预览后数据变化跳过对象，写入前新增占用转为冲突。</summary>
    [Theory]
    [InlineData(true, TagRebuildAction.Skipped)]
    [InlineData(false, TagRebuildAction.Conflict)]
    public void ChangesAfterPreviewPreventWrite(bool stale, TagRebuildAction action)
    {
        var host = new Host { Stale = stale, Occupied = !stale };
        var result = TagRebuildBatch.Run(Free(Item()), host, new Journal(), (_, _) => { });
        Assert.Equal(action, Assert.Single(result.Records).Action);
        Assert.DoesNotContain(host.Events, x => x.StartsWith("write:"));
    }

    /// <summary>回读不符保留实际值，不声称修改已回滚；继续下一对象。</summary>
    [Fact]
    public void ReadbackMismatchIsFailureAndNextObjectContinues()
    {
        var host = new Host { Write = x => x.Key == "PnId:1" ? "PT-actual" : x.Check.ExpectedTag };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, new Journal(), (_, _) => { });
        Assert.Equal(TagRebuildAction.Failed, result.Records[0].Action);
        Assert.Contains("PT-actual", result.Records[0].Reason);
        Assert.Equal(TagRebuildAction.Updated, result.Records[1].Action);
    }

    /// <summary>可回读的 SDK 写入错误按对象记录，包括重复位号错误。</summary>
    [Fact]
    public void KnownWriteErrorContinues()
    {
        var host = new Host { Write = x => x.Key == "PnId:1"
            ? throw new TagRebuildWriteException("DLM_DUPLICATED_TAG；实际 Tag：PT-101", true, new Exception())
            : x.Check.ExpectedTag };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, new Journal(), (_, _) => { });
        Assert.Contains("DLM_DUPLICATED_TAG", result.Records[0].Reason);
        Assert.Equal(TagRebuildAction.Updated, result.Records[1].Action);
    }

    /// <summary>无法回读异常后的状态时停止，后续对象明确为未执行。</summary>
    [Fact]
    public void UnknownWriteStateStopsAndClosesUndo()
    {
        var host = new Host { Write = _ => throw new TagRebuildWriteException("无法回读", false, new Exception()) };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, new Journal(), (_, _) => { });
        Assert.Equal(TagRebuildAction.Failed, result.Records[0].Action);
        Assert.Equal(TagRebuildAction.Unexecuted, result.Records[1].Action);
        Assert.False(result.SafeToSave);
        Assert.Equal("end", host.Events[^1]);
    }

    /// <summary>写入中的 Esc 记录该对象失败，未执行项单列，撤销组完成收尾。</summary>
    [Fact]
    public void CancellationDuringWritePreservesPartialState()
    {
        var host = new Host { Write = x => x.Key == "PnId:1" ? x.Check.ExpectedTag
            : throw new OperationCanceledException("实际 Tag：PT-103") };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104"), Item("PnId:3", next: "PT-105")),
            host, new Journal(), (_, _) => { });
        Assert.Equal(new[] { TagRebuildAction.Updated, TagRebuildAction.Failed, TagRebuildAction.Unexecuted },
            result.Records.Select(x => x.Action));
        Assert.Contains("已中断", result.StopReason);
        Assert.True(result.SafeToSave);
        Assert.Contains("end", host.Events);
    }

    /// <summary>图纸或项目变化在下一对象前阻止写入，收尾失败禁止保存。</summary>
    [Fact]
    public void ContextChangeStops()
    {
        var host = new Host { Context = n => { if (n == 4) throw new InvalidOperationException("原图纸已关闭"); } };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, new Journal(), (_, _) => { });
        Assert.Single(host.Events, x => x.StartsWith("write:"));
        Assert.False(result.SafeToSave);
        Assert.Contains("原图纸已关闭", result.StopReason);
    }

    /// <summary>最终日志写入失败后不再修改下一对象，仍结束撤销组。</summary>
    [Fact]
    public void JournalFailureStopsFurtherWrites()
    {
        var host = new Host();
        var log = new Journal { FailAppend = true };
        var result = TagRebuildBatch.Run(Free(Item(), Item("PnId:2", next: "PT-104")), host, log, (_, _) => { });
        Assert.Single(host.Events, x => x.StartsWith("write:"));
        Assert.Contains("end", host.Events);
        Assert.False(result.SafeToSave);
        Assert.Equal(TagRebuildAction.Updated, result.Records[0].Action);
        Assert.Equal(TagRebuildAction.Unexecuted, result.Records[1].Action);
    }

    /// <summary>写入前的日志故障必须零修改。</summary>
    [Fact]
    public void BeforeWriteJournalFailureHasNoMutation()
    {
        var host = new Host();
        var result = TagRebuildBatch.Run(Free(Item()), host, new Journal { FailMessage = true }, (_, _) => { });
        Assert.DoesNotContain(host.Events, x => x.StartsWith("write:"));
        Assert.Contains("end", host.Events);
        Assert.False(result.SafeToSave);
    }

    /// <summary>END 失败时已更新记录仍保留，禁止后续自动保存。</summary>
    [Fact]
    public void EndFailureDisablesSave()
    {
        var result = TagRebuildBatch.Run(Free(Item()), new Host { EndFailure = true }, new Journal(), (_, _) => { });
        Assert.Equal(TagRebuildAction.Updated, Assert.Single(result.Records).Action);
        Assert.False(result.SafeToSave);
        Assert.Contains("撤销组收尾失败", result.StopReason);
    }

    /// <summary>默认目录使用文档路径，不创建目录，时间戳冲突递增。</summary>
    [Fact]
    public void SuggestedDirectoryIsLazyAndUnique()
    {
        string root = Path.Combine(Path.GetTempPath(), "Pn3DKit-Rebuild-" + Guid.NewGuid());
        try
        {
            string first = TagRebuildPaths.Suggest(root, new(2026, 10, 25, 1, 2, 3));
            Assert.EndsWith(Path.Combine("Pn3DKit", "TagRebuild", "261025010203"), first);
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(first);
            Assert.Equal(first + "_01", TagRebuildPaths.Suggest(root, new(2026, 10, 25, 1, 2, 3)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>两个 FILEDIA 分支只在明确 C 时显示选择器；取消不创建日志。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DirectorySelectionHonorsFileDia(int fileDia)
    {
        string root = Path.Combine(Path.GetTempPath(), "Pn3DKit-Rebuild-" + Guid.NewGuid());
        bool dialog = false;
        string? output = OutputDirectorySelection.Select(fileDia, root, _ => { dialog = true; return root; },
            _ => fileDia == 0 ? "" : "C", _ => { }, TagRebuildPaths.Validate);
        Assert.Equal(root, output);
        Assert.Equal(fileDia != 0, dialog);
        Assert.False(Directory.Exists(root));
        Assert.Null(OutputDirectorySelection.Select(fileDia, root, _ => null, _ => null, _ => { }, TagRebuildPaths.Validate));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>CSV 带 BOM、固定七列和 RFC4180 转义，追加历史立即可读。</summary>
    [Fact]
    public void JournalIsFlushedAppendOnlyAndNeverOverwrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "Pn3DKit-Rebuild-" + Guid.NewGuid());
        try
        {
            using (var log = new TagRebuildJournal(root, "图纸.dwg", ["仪表"]))
            {
                var item = Item(old: "原,\"号\n中文", next: "新位号");
                log.Append(new(item, TagRebuildAction.Pending, "预览"));
                log.Append(new(item, TagRebuildAction.Updated, "已回读"));
                using var csvReader = new FileStream(log.CsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var buffer = new MemoryStream();
                csvReader.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();
                Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
                string csv = Encoding.UTF8.GetString(bytes[3..]);
                Assert.StartsWith("项目部分,类别,数据行ID,句柄,原Tag,新Tag,动作,原因,差异字段\r\n", csv);
                Assert.Contains("\"原,\"\"号\n中文\"", csv);
                Assert.Contains("待重建", csv);
                Assert.Contains("已更新", csv);
                using var logReader = new StreamReader(new FileStream(log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                Assert.Contains("已回读", logReader.ReadToEnd());
                Assert.Throws<IOException>(() => new TagRebuildJournal(root, "other", []));
            }
            Assert.Throws<IOException>(() => TagRebuildPaths.Validate(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>任意一件日志已存在都拒绝创建，并保留原内容。</summary>
    [Theory]
    [InlineData("TagRebuild.log")]
    [InlineData("TagRebuild.csv")]
    public void ExistingLogPreventsBatch(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "Pn3DKit-Rebuild-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, name), "保留");
            Assert.Throws<IOException>(() => new TagRebuildJournal(root, "图纸", []));
            Assert.Equal("保留", File.ReadAllText(Path.Combine(root, name)));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>保存前必须明确确认和上下文已知；不确认不调用 QSAVE。</summary>
    [Fact]
    public async Task SaveRequiresConfirmationAndKnownState()
    {
        int calls = 0;
        Task Save() { calls++; return Task.CompletedTask; }
        Assert.False(await TagRebuildSave.RunAsync(false, true, Save, () => Task.FromResult(false), new Journal()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TagRebuildSave.RunAsync(true, false, Save, () => Task.FromResult(false), new Journal()));
        Assert.Equal(0, calls);
        Assert.True(await TagRebuildSave.RunAsync(true, true, Save, () => Task.FromResult(false), new Journal()));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TagRebuildSave.RunAsync(true, true, Save, () => Task.FromResult(true), new Journal()));
    }

    /// <summary>异步保存未结束时租约仍阻止其他批次；释放后可重新申请且重复释放无害。</summary>
    [Fact]
    public async Task SharedGateCoversAsyncSaveAndCleanup()
    {
        var lease = BatchRunGate.TryEnter();
        Assert.NotNull(lease);
        var completion = new TaskCompletionSource();
        try
        {
            var save = TagRebuildSave.RunAsync(true, true, () => completion.Task,
                () => Task.FromResult(false), new Journal());
            Assert.False(save.IsCompleted);
            Assert.Null(BatchRunGate.TryEnter());
            completion.SetResult();
            Assert.True(await save);
            Assert.True(BatchRunGate.IsRunning);
        }
        finally { lease.Dispose(); }
        using var second = BatchRunGate.TryEnter();
        Assert.NotNull(second);
        lease.Dispose();
        Assert.True(BatchRunGate.IsRunning);
    }

    /// <summary>离线宿主记录生命周期，可注入真实失败类别而非依赖 SDK。</summary>
    private sealed class Host : ITagRebuildHost
    {
        internal readonly List<string> Events = [];
        internal bool BeginFailure, EndFailure, Stale, Occupied;
        internal Action<int>? Context;
        internal Func<TagRebuildCandidate, string>? Write;
        private int _checks;
        public void EnsureContext() => Context?.Invoke(++_checks);
        public void BeginUndo() { if (BeginFailure) throw new InvalidOperationException("UNDO 不可用"); Events.Add("begin"); }
        public void EndUndo() { Events.Add("end"); if (EndFailure) throw new InvalidOperationException("END 状态不明"); }
        public string? Revalidate(TagRebuildCandidate item) => Stale ? "预检后属性变化" : null;
        public IReadOnlyList<string> FindOwners(TagRebuildCandidate item) => Occupied ? ["PnId:99"] : [];
        public string WriteAndRead(TagRebuildCandidate item)
        { Events.Add("write:" + item.Key); return Write?.Invoke(item) ?? item.Check.ExpectedTag; }
        public void Regen() => Events.Add("regen");
    }

    /// <summary>离线日志捕获追加历史，可模拟磁盘写入故障。</summary>
    private sealed class Journal : ITagRebuildJournal
    {
        internal readonly List<TagRebuildRecord> Records = [];
        internal bool FailAppend, FailMessage;
        public void Append(TagRebuildRecord record)
        { if (FailAppend) throw new IOException("日志磁盘已满"); Records.Add(record); }
        public void Message(string message)
        { if (FailMessage) throw new IOException("日志磁盘已满"); }
    }
}
