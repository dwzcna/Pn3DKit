using Pn3DKit.Services;

namespace Pn3DKit.Tests;

/// <summary>验证导出专用变量保护的读写顺序、失败恢复及未知状态，不连接 CAD。</summary>
public sealed class ExportAutoCheckScopeTests
{
    /// <summary>可注入部分写入失败及回读故障的系统变量替身。</summary>
    private sealed class Variable
    {
        internal object? Value = 1;
        internal int Reads;
        internal readonly List<object> Writes = [];
        internal Func<int, object?>? OnRead;
        internal Action<object, int>? OnWrite;
        /// <summary>每次读取都计数；故障委托返回 null 时也保留该真实返回值。</summary>
        internal object? Read()
        {
            int count = ++Reads;
            return OnRead != null ? OnRead(count) : Value;
        }
        internal void Write(object value)
        {
            Writes.Add(value);
            if (OnWrite != null) OnWrite(value, Writes.Count); else Value = value;
        }
        internal ExportAutoCheckScope Scope() => new(Read, Write);
    }

    /// <summary>既有零值、整数及 SDK short 原值都原样恢复，重复结束不再次写入。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData((short)1)]
    public void DisablesConfirmsAndRestoresOriginalBoxedValue(object original)
    {
        var variable = new Variable { Value = original }; var scope = variable.Scope();
        Assert.Contains(ExportAutoCheckScope.VariableName, scope.Disable());
        Assert.Equal(0, variable.Value); Assert.Equal(2, variable.Reads);
        var outcome = scope.Finish(null);
        Assert.True(outcome.Restored); Assert.False(outcome.HasIssue);
        Assert.Same(original, variable.Writes[1]); Assert.Equal(original, variable.Value);
        Assert.Same(outcome, scope.Finish(null)); Assert.Equal(2, variable.Writes.Count);
    }

    /// <summary>缺失变量或原值读取失败不写入，不以默认零冒充有效变量。</summary>
    [Fact]
    public void MissingVariableStopsBeforeAnyWrite()
    {
        var variable = new Variable { OnRead = _ => throw new InvalidOperationException("eInvalidInput") };
        var scope = variable.Scope(); var error = Assert.Throws<InvalidOperationException>(() => scope.Disable());
        Assert.Contains("eInvalidInput", error.Message); Assert.Contains(ExportAutoCheckScope.VariableName, error.Message);
        Assert.Empty(variable.Writes); Assert.False(scope.Finish(null).Restored);
    }

    /// <summary>小数、文本及 null 原值均拒绝，防止转换取整后把未关闭误判为零。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData(0.4)]
    public void InvalidOriginalValueNeverStartsExport(object? original)
    {
        var scope = new ExportAutoCheckScope(() => original, _ => Assert.Fail("不应写入无效变量。"));
        Assert.Throws<InvalidOperationException>(() => scope.Disable());
        Assert.False(scope.Finish(null).Restored);
    }

    /// <summary>设置失败可能已改变值；写入前登记责任，初始异常保留而恢复仍执行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDisableStillRestoresAfterPartialSdkWrite(bool changed)
    {
        var variable = new Variable();
        variable.OnWrite = (value, count) =>
        {
            if (count == 1) { if (changed) variable.Value = value; throw new IOException("SDK 写入失败"); }
            variable.Value = value;
        };
        var scope = variable.Scope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.Disable());
        Assert.IsType<IOException>(error.InnerException);
        Assert.True(scope.Finish(null).Restored); Assert.Equal(1, variable.Value);
        Assert.Contains("SDK 写入失败", error.Message);
    }

    /// <summary>写后回读不符、无效或抛错都阻止后续处理，并尝试恢复原值。</summary>
    [Theory]
    [InlineData("different")]
    [InlineData("fraction")]
    [InlineData("exception")]
    public void DisableReadbackFailureStopsAndRestores(string failure)
    {
        var variable = new Variable();
        variable.OnRead = count => count != 2 ? variable.Value : failure switch
        {
            "different" => 1, "fraction" => 0.2,
            _ => throw new IOException("回读错误")
        };
        var scope = variable.Scope();
        Assert.Throws<InvalidOperationException>(() => scope.Disable());
        Assert.True(scope.Finish(null).Restored); Assert.Equal(1, variable.Value);
    }

    /// <summary>恢复写入、回读失败及不符均返回单独问题，不让收尾异常覆盖原错误。</summary>
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("different")]
    public void RestoreFailureIsReportedWithoutThrowing(string failure)
    {
        var variable = new Variable();
        variable.OnWrite = (value, count) =>
        {
            if (count == 2 && failure == "write") throw new IOException("恢复写入失败");
            variable.Value = value;
        };
        variable.OnRead = count => count == 3 ? failure switch
        {
            "read" => throw new IOException("恢复回读失败"), "different" => 0, _ => variable.Value
        } : variable.Value;
        var scope = variable.Scope(); scope.Disable();
        var outcome = scope.Finish(null);
        Assert.True(outcome.HasIssue); Assert.False(outcome.Restored);
        Assert.Contains(ExportAutoCheckScope.VariableName, outcome.Message); Assert.Contains("原值 1", outcome.Message);
        Assert.Same(outcome, scope.Finish(null)); Assert.Equal(2, variable.Writes.Count);
    }

    /// <summary>超时或清理未知时不读取也不恢复，避免原生命令仍运行时重新启用检查。</summary>
    [Theory]
    [InlineData("原生命令可能仍运行")]
    [InlineData("LISP 等待器清理失败")]
    public void UnknownNativeStateKeepsCheckerDisabledWithoutSdkAccess(string reason)
    {
        var variable = new Variable(); var scope = variable.Scope(); scope.Disable();
        int reads = variable.Reads; var outcome = scope.Finish(reason);
        Assert.True(outcome.HasIssue); Assert.False(outcome.Restored);
        Assert.Equal(0, variable.Value); Assert.Single(variable.Writes); Assert.Equal(reads, variable.Reads);
        Assert.Contains(reason, outcome.Message); Assert.Contains("原值 1", outcome.Message);
        Assert.Same(outcome, scope.Finish(null)); Assert.Single(variable.Writes);
    }

    /// <summary>主线程之外的恢复只报告问题，不执行任何 SDK 委托。</summary>
    [Fact]
    public void RestoreOnWrongThreadDoesNotTouchSdk()
    {
        var variable = new Variable(); var scope = variable.Scope(); scope.Disable();
        ExportAutoCheckOutcome? outcome = null;
        var worker = new Thread(() => outcome = scope.Finish(null)); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.NotNull(outcome); Assert.True(outcome.HasIssue); Assert.False(outcome.Restored);
        Assert.Contains("主线程", outcome.Message); Assert.Single(variable.Writes); Assert.Equal(2, variable.Reads);
    }

    /// <summary>尚未开始或重复开始均不能额外修改系统变量。</summary>
    [Fact]
    public void ScopeCannotBeStartedTwiceOrAfterFinishing()
    {
        var variable = new Variable(); var scope = variable.Scope();
        scope.Disable(); Assert.Throws<InvalidOperationException>(() => scope.Disable());
        Assert.Single(variable.Writes); scope.Finish(null);
        Assert.Throws<InvalidOperationException>(() => scope.Disable());
        var unused = variable.Scope(); Assert.False(unused.Finish(null).Restored);
        Assert.Throws<InvalidOperationException>(() => unused.Disable());
    }
}
