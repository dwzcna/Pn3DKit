using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>多选共有属性的实际策略与批次宿主测试，不引用 Autodesk，不代替 CAD 写入验收。</summary>
public sealed class PnPropertyEditTests
{
    private static readonly PnPropertySpec Spec = new(Guid.NewGuid(), 1, "installed");
    private static PnObjectProperties Object(string type, string value, PnEditPermission? permission = null,
        FastPropertyValue? state = null) => new(type, type,
            [new(Spec, state ?? FastPropertyValue.Value(value), permission ?? PnEditPermission.Writable)],
            "PnId:" + type, "句柄 " + type);

    /// <summary>不同类别的共有可写安装位置允许统一编辑，当前值不同不阻止输入。</summary>
    [Fact]
    public void ValveAndFlowMeterAllowTheirCommonWritableInstalledProperty()
    {
        var result = PnPropertyPolicy.Merge([Spec], [Object("手动阀", "一层"), Object("流量计", "二层")]);
        Assert.Equal("多种值", result.Cells[0].Value.Display);
        Assert.True(result.Cells[0].Permission.Allowed); Assert.Equal(2, result.Count);
        Assert.Equal("多种对象（2 个对象）", result.Header);
    }

    /// <summary>共同空值可编辑，仍由原始空值比较，不把显示标记当值。</summary>
    [Fact]
    public void AllEmptyPropertiesRemainWritableAndBlank()
    {
        var result = PnPropertyPolicy.Merge([Spec], [Object("手动阀", ""), Object("流量计", "")]);
        Assert.True(result.Cells[0].Permission.Allowed); Assert.Equal("", result.Cells[0].Value.Display);
    }

    /// <summary>任一对象的获取、锁层、只读、缺少或读取错误，均使整行只读并保留目标原因。</summary>
    [Theory]
    [InlineData("获取属性")]
    [InlineData("图层已锁定")]
    [InlineData("SDK 字段为只读")]
    [InlineData("属性不存在")]
    [InlineData("SDK ErrorStatus=InvalidInput")]
    [InlineData("非 Plant 对象")]
    public void AnyIneligibleSelectedObjectDeniesTheWholeField(string reason)
    {
        var state = reason == "属性不存在" ? FastPropertyValue.AbsentProperty(reason)
            : reason.Contains("ErrorStatus") ? FastPropertyValue.Error(reason)
            : reason == "非 Plant 对象" ? FastPropertyValue.Missing(reason) : FastPropertyValue.Value("DN50");
        var result = PnPropertyPolicy.Merge([Spec],
            [Object("流量计", "DN50"), Object("手动阀或设备", "DN50", PnEditPermission.ReadOnly(reason), state)]);
        Assert.False(result.Cells[0].Permission.Allowed);
        Assert.Contains(reason, result.Cells[0].Permission.Reason); Assert.Contains("手动阀或设备", result.Cells[0].Permission.Reason);
        if (reason == "属性不存在") Assert.Equal("--", result.Cells[0].Value.Display);
    }

    /// <summary>引用真实元数据策略，获取 Size 不因其他对象可写而放开。</summary>
    [Fact]
    public void AcquiredSizeIsDeniedByMetadataBeforeAggregation()
    {
        var denied = PnPropertyMetadataPolicy.Evaluate(new("Size"), [], _ => null, () => true, () => "Acquired",
            () => throw new InvalidOperationException("普通字段不应查询 Tag"));
        var result = PnPropertyPolicy.Merge([Spec], [Object("流量计", "DN50"), Object("手动阀", "DN50", denied)]);
        Assert.False(result.Cells[0].Permission.Allowed); Assert.Contains("获取属性", result.Cells[0].Permission.Reason);
    }

    /// <summary>完整选择集合按对象身份比较，顺序变化不算换选，超过一批也不截断。</summary>
    [Fact]
    public void SelectionOrderDoesNotInvalidateAndLargeSelectionIsComplete()
    {
        var host = new Host(70) { ReverseCapture = true };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "统一位置", host);
        Assert.True(result.PreflightPassed); Assert.Equal(70, host.Writes.Count);
        Assert.All(result.Items, x => { Assert.Equal(PnPropertyWriteState.Updated, x.State); Assert.Equal("统一位置", x.Actual); });
        Assert.Equal(70, host.PreparedBeforeFirstWrite);
    }

    /// <summary>原值、权限、身份、行修订和目标增减中的任意过期状态，首笔写入前整体拒绝。</summary>
    [Theory]
    [InlineData("original")]
    [InlineData("permission")]
    [InlineData("identity")]
    [InlineData("epoch")]
    [InlineData("revision")]
    [InlineData("path")]
    [InlineData("link")]
    [InlineData("target")]
    [InlineData("count")]
    public void StaleSnapshotRejectsEveryWrite(string change)
    {
        var host = new Host(2); var snapshot = host.Snapshot();
        if (change == "original") host.Values[2] = "外部修改";
        else if (change == "count") host.Current.RemoveAt(1);
        else host.Current[1] = change switch
        {
            "permission" => host.Current[1] with { Permission = PnEditPermission.ReadOnly("图层锁定") },
            "identity" => host.Current[1] with { Identity = 99 },
            "epoch" => host.Current[1] with { Epoch = 9 },
            "revision" => host.Current[1] with { RowRevision = 9 },
            "path" => host.Current[1] with { Path = "TargetObject.Size" },
            "link" => host.Current[1] with { TargetKey = "PnId:99:Valve" },
            _ => host.Current[1] with { Target = 99 }
        };
        host.Values[99] = "位置2";
        var result = PnPropertyEditBatch.Run(snapshot, "统一位置", host);
        Assert.False(result.PreflightPassed); Assert.Empty(host.Writes);
        Assert.All(result.Items, x => Assert.Equal(PnPropertyWriteState.NotExecuted, x.State));
    }

    /// <summary>第二个对象的新值类型／长度预检失败，前一个对象也不写。</summary>
    [Fact]
    public void AnyPrepareFailurePreventsTheFirstWrite()
    {
        var host = new Host(3) { PrepareFailure = 2 };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "超长新值", host);
        Assert.False(result.PreflightPassed); Assert.Empty(host.Writes);
        Assert.Contains("预检", result.Message); Assert.Contains("句柄 2", result.Message);
    }

    /// <summary>项目占用及批内位号登记冲突均在首笔写入前拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TagConflictsRejectTheWholeSubmission(bool projectConflict)
    {
        var host = new Host(2) { RegisterTags = true, ProjectConflict = projectConflict };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "PT-101", host);
        Assert.False(result.PreflightPassed); Assert.Empty(host.Writes); Assert.Contains("冲突", result.Message);
    }

    /// <summary>相同显示 Tag 在不同注册表不构成仅按文字判断的重号。</summary>
    [Fact]
    public void SeparateRegistriesAllowEqualDisplayValues()
    {
        var host = new Host(2) { RegisterTags = true, SeparateRegistries = true };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "相同显示值", host);
        Assert.True(result.PreflightPassed); Assert.Equal(2, host.Writes.Count);
    }

    /// <summary>同一实际行的图形别名只写一次，所有对象仍各自核对权限及回读。</summary>
    [Fact]
    public void AliasesOfOneDataRowAreWrittenOnce()
    {
        var host = new Host(2) { RegisterTags = true };
        host.Current[1] = host.Current[1] with { TargetKey = host.Current[0].TargetKey };
        host.Values[2] = host.Values[1];
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "新位置", host);
        Assert.True(result.PreflightPassed); Assert.Single(host.Writes); Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, x => Assert.Equal(PnPropertyWriteState.Updated, x.State));
    }

    /// <summary>实际写入失败即停止，保留前一个成功项及后一个未执行项。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteFailureStopsAndReportsActualPartialResults(bool failAfterChange)
    {
        var host = new Host(3) { WriteFailure = 2, FailAfterChange = failAfterChange };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "新位置", host);
        Assert.Equal(new[] { 1, 2 }, host.Writes);
        Assert.Equal(PnPropertyWriteState.Updated, result.Items[0].State);
        Assert.Equal(PnPropertyWriteState.Failed, result.Items[1].State);
        Assert.Equal(PnPropertyWriteState.NotExecuted, result.Items[2].State);
        Assert.Equal(failAfterChange ? "新位置" : "位置2", result.Items[1].Actual);
        Assert.Contains("DLM_DUPLICATED_TAG", result.Items[1].Detail);
        Assert.Contains("不假定", result.Message); Assert.Contains("未执行 1", result.Details);
    }

    /// <summary>回读不符同样停止，不把第一笔成功掩盖为整体回滚。</summary>
    [Fact]
    public void ReadbackMismatchStopsRemainingTargets()
    {
        var host = new Host(3) { MismatchedWrite = 2 };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "新位置", host);
        Assert.Equal(2, host.Writes.Count); Assert.Equal("SDK 实际值", result.Items[1].Actual);
        Assert.Equal(PnPropertyWriteState.NotExecuted, result.Items[2].State);
    }

    /// <summary>已成功的自身修改更新预期原值；其他对象意外变化必须停止后续。</summary>
    [Fact]
    public void UnexpectedChangeToAnotherSelectedObjectStopsNextWrite()
    {
        var host = new Host(3);
        host.AfterWrite = id => { if (id == 1) host.Values[2] = "外部修改"; };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "新位置", host);
        Assert.Single(host.Writes); Assert.Equal(PnPropertyWriteState.Updated, result.Items[0].State);
        Assert.Equal(PnPropertyWriteState.Failed, result.Items[1].State); Assert.Equal("外部修改", result.Items[1].Actual);
        Assert.Equal(PnPropertyWriteState.NotExecuted, result.Items[2].State);
    }

    /// <summary>不重复写已等于新值的对象，空值写入也不生成显示占位文本。</summary>
    [Fact]
    public void EmptyAndUnchangedValuesAreHandledWithoutPlaceholderWrites()
    {
        var host = new Host(3); host.Values[2] = "";
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "", host);
        Assert.Equal(new[] { 1, 3 }, host.Writes); Assert.Equal(PnPropertyWriteState.Unchanged, result.Items[1].State);
        Assert.All(host.Values.Values, value => Assert.Equal("", value));
    }

    /// <summary>最后一笔引发前一目标的非预期变化时，最终回读不能继续显示全部成功。</summary>
    [Fact]
    public void FinalRecheckReportsAnyPreviouslyWrittenValueThatChangedAgain()
    {
        var host = new Host(2); host.AfterWrite = id => { if (id == 2) host.Values[1] = "再次变化"; };
        var result = PnPropertyEditBatch.Run(host.Snapshot(), "新位置", host);
        Assert.Equal(2, host.Writes.Count); Assert.Equal(PnPropertyWriteState.Failed, result.Items[0].State);
        Assert.Equal("再次变化", result.Items[0].Actual); Assert.Equal(PnPropertyWriteState.Updated, result.Items[1].State);
        Assert.Contains("最终复核", result.Message);
    }

    private sealed class Host : IPnPropertyEditHost<int, int>
    {
        internal List<PnEditContext<int, int>> Current { get; } = [];
        internal Dictionary<int, string> Values { get; } = [];
        internal List<int> Writes { get; } = [];
        internal bool ReverseCapture, RegisterTags, SeparateRegistries, ProjectConflict, FailAfterChange;
        internal int PrepareFailure, WriteFailure, MismatchedWrite, PreparedBeforeFirstWrite;
        internal Action<int>? AfterWrite;
        private readonly HashSet<int> _prepared = [];

        internal Host(int count)
        {
            for (int id = 1; id <= count; id++)
            {
                Values[id] = "位置" + id;
                Current.Add(new(1, id, 1, Spec.Id, 1, "TargetObject.installed", "PnId:" + id + ":Valve",
                    Values[id], count, PnEditPermission.Writable));
            }
        }
        internal PnEditContext<int, int>[] Snapshot() => Capture().ToArray();
        public IReadOnlyList<PnEditContext<int, int>> Capture()
        {
            var items = Current.Select(x => x with { Original = Values[x.Target] }).ToArray();
            return ReverseCapture ? items.Reverse().ToArray() : items;
        }
        public PnPropertyWritePlan<int> Prepare(PnEditContext<int, int> target, string value)
        {
            _prepared.Add(target.Target);
            if (target.Target == PrepareFailure) throw new InvalidOperationException(Label(target.Target) + "：字段长度超限");
            if (ProjectConflict) throw new InvalidOperationException("项目登记冲突");
            return new(target.Target, target.TargetKey, target.Original, value, RegisterTags
                ? new("PnId", SeparateRegistries ? "Registry" + target.Target : "Registry", value) : null);
        }
        public string Write(PnPropertyWritePlan<int> plan)
        {
            if (Writes.Count == 0) PreparedBeforeFirstWrite = _prepared.Count;
            Writes.Add(plan.Target);
            if (plan.Target == WriteFailure && !FailAfterChange) throw new InvalidOperationException("DLM_DUPLICATED_TAG");
            string actual = plan.Target == MismatchedWrite ? "SDK 实际值" : plan.Value;
            foreach (var alias in Current.Where(x => x.TargetKey == plan.DataKey)) Values[alias.Target] = actual;
            AfterWrite?.Invoke(plan.Target);
            if (plan.Target == WriteFailure) throw new InvalidOperationException("DLM_DUPLICATED_TAG");
            return actual;
        }
        public FastPropertyValue ReadActual(int target) => FastPropertyValue.Value(Values[target]);
        public string Label(int target) => "句柄 " + target;
    }
}
