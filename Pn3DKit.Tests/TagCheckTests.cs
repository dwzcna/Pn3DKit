using System.Text;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>离线覆盖位号重建、编号选择、即时日志及取消；不加载 Autodesk。</summary>
public sealed class TagCheckTests
{
    private const string Format = "#(+TargetObject.Type)-#(TargetObject.LoopNumber^NNN)";

    /// <summary>构造保留原始 Tag 的离线检查输入。</summary>
    private static TagCheckSubject Subject(string tag = "PT-101", string format = Format) =>
        new("PnId", "Instrument", "AB", format, tag);

    /// <summary>构造属性快照，格式化片段可与原始属性不同。</summary>
    private static TagCheckField[] Fields(string type = "PT", string rawNumber = "102", string number = "102") =>
    [
        new("#(+TargetObject.Type)", "TargetObject.Type", "PT", type, type, false),
        new("#(TargetObject.LoopNumber^NNN)", "TargetObject.LoopNumber", "101", rawNumber, number, false)
    ];

    /// <summary>编号支持范围、空格、重复去重及全选，结果稳定升序。</summary>
    [Theory]
    [InlineData("1,3,5-7,3", "1,3,5,6,7")]
    [InlineData(" 2 - 4 , 1 ", "1,2,3,4")]
    [InlineData("*", "1,2,3,4,5,6,7")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("0", "")]
    public void ClassNumbersAreValidatedAndDeduplicated(string input, string expected) =>
        Assert.Equal(expected, string.Join(",", TagCheckPolicy.ParseClasses(input, 7)));

    /// <summary>无效编号必须给出原因，不能静默忽略部分输入。</summary>
    [Theory]
    [InlineData("7-5", "反向")]
    [InlineData("8", "1 到 7")]
    [InlineData("1,0", "0 只能")]
    [InlineData("1,,3", "非法")]
    [InlineData("*,1", "非法")]
    [InlineData("abc", "非法")]
    [InlineData("-1", "非法")]
    [InlineData("1.5", "非法")]
    [InlineData("99999999999999999", "过大")]
    public void InvalidClassNumbersExplainWhy(string input, string reason) =>
        Assert.Contains(reason, Assert.Throws<ArgumentException>(() => TagCheckPolicy.ParseClasses(input, 7)).Message);

    /// <summary>位号不符时输出当前属性及原片段，最终结果与单个命令一致。</summary>
    [Fact]
    public void ChangedPropertyReportsExpectedTagAndField()
    {
        var result = TagCheckPolicy.Check(Subject(), () => "PT-102", () => Fields());
        Assert.Equal(TagCheckStatus.Mismatch, result.Status);
        Assert.Equal("PT-102", result.ExpectedTag);
        Assert.Equal("TargetObject.LoopNumber：Tag 中为 101，当前属性为 102", Assert.Single(result.Differences).ToString());
        Assert.Equal("位号不符：现有 PT-101；按格式应为 PT-102", result.Message);
    }

    /// <summary>原始数字 1 按 NNN 格式输出 001，不把补零当作差异。</summary>
    [Fact]
    public void FormattingDoesNotCreateFalseMismatch()
    {
        var fields = Fields(rawNumber: "1", number: "001");
        fields[1] = fields[1] with { ExistingValue = "001" };
        var result = TagCheckPolicy.Check(Subject("PT-001"), () => "PT-001", () => fields);
        Assert.Equal(TagCheckStatus.Consistent, result.Status);
        Assert.Empty(result.Differences);
    }

    /// <summary>自动编号始终保留旧片段，即便 SDK 返回了不同编号。</summary>
    [Fact]
    public void AutomaticNumberIsRetainedWhenSdkDisagrees()
    {
        const string format = "#(+TargetObject.Type)-#(TargetObject.Number^@NNN$Project.Last)";
        TagCheckField[] fields =
        [
            new("#(+TargetObject.Type)", "TargetObject.Type", "PT", "TT", "TT", false),
            new("#(TargetObject.Number^@NNN$Project.Last)", "TargetObject.Number", "101", "999", "999", true)
        ];
        var result = TagCheckPolicy.Check(Subject(format: format), () => "TT-999", () => fields);
        Assert.Equal("TT-101", result.ExpectedTag);
        Assert.Equal("TargetObject.Type", Assert.Single(result.Differences).Property);
        Assert.Contains("逐字段", result.Note);
    }

    /// <summary>SDK 重建失败后仍可使用已验证的字段重建，保留 SDK 状态供诊断。</summary>
    [Fact]
    public void FailedSdkRebuildFallsBackWithoutLosingDiagnostic()
    {
        var result = TagCheckPolicy.Check(Subject(),
            () => throw new InvalidOperationException("SDK ErrorStatus=InvalidInput"), () => Fields());
        Assert.Equal(TagCheckStatus.Mismatch, result.Status);
        Assert.Contains("InvalidInput", result.Note);
    }

    /// <summary>字段边界不可靠时报告错误，不因 SDK 返回旧 Tag 而误报一致。</summary>
    [Fact]
    public void AmbiguousTokenizationIsAnError()
    {
        var fields = Fields();
        fields[0] = fields[0] with { ExistingValue = "PT1" };
        var result = TagCheckPolicy.Check(Subject(), () => "PT-101", () => fields);
        Assert.Equal(TagCheckStatus.Error, result.Status);
        Assert.Contains("无法按格式可靠拆分", result.Error);
        Assert.Empty(result.ExpectedTag);
    }

    /// <summary>完整类别上下文保持不变，阀门安装管线和对象自身不会被合并。</summary>
    [Fact]
    public void DifferentPropertyScopesRemainDistinct()
    {
        const string format = "#(PipeLines.Size)/#(TargetObject.Size)";
        TagCheckField[] fields =
        [
            new("#(PipeLines.Size)", "PipeLines.Size", "50", "80", "80", false),
            new("#(TargetObject.Size)", "TargetObject.Size", "25", "25", "25", false)
        ];
        var result = TagCheckPolicy.Check(Subject("50/25", format), () => "80/25", () => fields);
        Assert.Equal("PipeLines.Size", Assert.Single(result.Differences).Property);
        Assert.Equal("80/25", result.ExpectedTag);
    }

    /// <summary>属性缺失保留完整路径和 SDK 状态，不把未知值拼成空字符串。</summary>
    [Fact]
    public void MissingPropertyRetainsSdkStatus()
    {
        var result = TagCheckPolicy.Check(Subject(), () => throw new InvalidOperationException("InvalidInput"),
            () => throw new InvalidOperationException("PipeLineGroup.Size：SDK ErrorStatus=KeyNotFound"));
        Assert.Equal(TagCheckStatus.Error, result.Status);
        Assert.Contains("PipeLineGroup.Size", result.Details);
        Assert.Contains("KeyNotFound", result.Details);
    }

    /// <summary>无格式、无 Tag 和临时 Tag 均不调用 SDK 或产生新编号。</summary>
    [Theory]
    [InlineData("", "PT-101", TagCheckStatus.NoFormat)]
    [InlineData(Format, "", TagCheckStatus.NoTag)]
    [InlineData(Format, "  ", TagCheckStatus.NoTag)]
    [InlineData(Format, "?101", TagCheckStatus.TemporaryTag)]
    public void EarlyStatusesDoNotRebuild(string format, string tag, TagCheckStatus expected)
    {
        var result = TagCheckPolicy.Check(Subject(tag, format),
            () => throw new Xunit.Sdk.XunitException("不得重建"),
            () => throw new Xunit.Sdk.XunitException("不得读取字段"));
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.ExpectedTag);
    }

    /// <summary>最终比较区分大小写，取消必须传播给批次而非计为对象错误。</summary>
    [Fact]
    public void OrdinalComparisonAndCancellationArePreserved()
    {
        var result = TagCheckPolicy.Check(Subject(), () => "pt-101", () => Fields(type: "pt", rawNumber: "101", number: "101"));
        Assert.Equal(TagCheckStatus.Mismatch, result.Status);
        Assert.Throws<OperationCanceledException>(() => TagCheckPolicy.Check(Subject(),
            () => throw new OperationCanceledException(), () => Fields()));
    }

    /// <summary>Piping 的空 Tag 回退 LineNumberTag；PnId 不回退。</summary>
    [Theory]
    [InlineData("Piping", "", "L-100")]
    [InlineData("PnId", "", "")]
    [InlineData("Piping", "V-100", "V-100")]
    public void ExistingTagFallbackMatchesProjectPart(string part, string tag, string expected)
    {
        var calls = new List<string>();
        string actual = TagCheckPolicy.ReadExistingTag(part, name =>
        {
            calls.Add(name);
            return name == "Tag" ? tag : "L-100";
        });
        Assert.Equal(expected, actual);
        Assert.Equal(expected == "L-100" ? 2 : 1, calls.Count);
    }

    /// <summary>原格式的前后缀及可选空片段保持原位置。</summary>
    [Fact]
    public void LiteralsAndEmptyOptionalValueArePreserved()
    {
        const string format = "设备[#(TargetObject.Type)]#(TargetObject.Area)(备用)";
        TagCheckField[] fields =
        [
            new("#(TargetObject.Type)", "TargetObject.Type", "PT", "TT", "TT", false),
            new("#(TargetObject.Area)", "TargetObject.Area", "", "", "", false)
        ];
        Assert.Equal("设备[TT](备用)", TagCheckPolicy.Reconstruct(format, "设备[PT](备用)", fields));
    }

    /// <summary>建议目录使用传入的系统文档路径，文件及目录重名都递增编号。</summary>
    [Fact]
    public void SuggestedDirectoryUsesDocumentsAndCollisionSuffix()
    {
        using var fixture = new LogFixture();
        var now = new DateTime(2026, 10, 25, 14, 3, 7);
        string first = Path.Combine(fixture.Root, "Pn3DKit.TagCheck", "261025140307");
        Assert.Equal(first, TagCheckPaths.Suggest(fixture.Root, now));
        Assert.False(Directory.Exists(first));
        Directory.CreateDirectory(first);
        File.WriteAllText(first + "_01", "keep");
        Assert.Equal(first + "_02", TagCheckPaths.Suggest(fixture.Root, now));
    }

    /// <summary>日志目录验证只限制两件检查日志，不误用导出预留文件规则。</summary>
    [Theory]
    [InlineData("TagCheck.log")]
    [InlineData("TagCheck.csv")]
    public void LogConflictsNeverOverwriteExistingFiles(string name)
    {
        using var fixture = new LogFixture();
        Directory.CreateDirectory(fixture.Root);
        string original = Path.Combine(fixture.Root, name);
        File.WriteAllText(original, "用户原文件");
        Assert.Throws<IOException>(() => new TagCheckJournal(fixture.Root, "a.dwg", ["Instrument"], ""));
        Assert.Equal("用户原文件", File.ReadAllText(original));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    /// <summary>FILEDIA 自定义验证使用日志规则，冲突后重选，取消不创建默认目录。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DirectorySelectionUsesLogRulesAndRespectsCancellation(int fileDia)
    {
        using var fixture = new LogFixture();
        string occupied = Path.Combine(fixture.Root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "TagCheck.csv"), "keep");
        string target = Path.Combine(fixture.Root, "目标 目录");
        var inputs = new Queue<string?>(fileDia == 0 ? [occupied, target] : ["C", "C"]);
        var picked = new Queue<string>([occupied, target]);
        var errors = new List<string>();
        Assert.Equal(target, OutputDirectorySelection.Select(fileDia, target,
            _ => picked.Dequeue(), _ => inputs.Dequeue(), errors.Add, TagCheckPaths.Validate, "日志目录："));
        Assert.Single(errors);
        Assert.StartsWith("日志目录：", errors[0]);
        Assert.False(Directory.Exists(target));
        Assert.Null(OutputDirectorySelection.Select(fileDia, target, _ => null, _ => null,
            errors.Add, TagCheckPaths.Validate));
        Assert.False(Directory.Exists(target));
    }

    /// <summary>CSV 可由独立解析器回读引号、逗号和换行；在日志仍打开时即可读取完整记录。</summary>
    [Fact]
    public void CsvHasBomEscapesRfc4180AndFlushesEachResult()
    {
        using var fixture = new LogFixture();
        using var journal = new TagCheckJournal(fixture.Root, "中文 图纸.dwg", ["Instrument"], "");
        var result = new TagCheckResult(Subject("PT,\"101\"\r\n第二行"), "PT-102", TagCheckStatus.Mismatch,
            [new("TargetObject.Area", "A,1", "B\"2\n新行")]);
        journal.Append(result);
        byte[] bytes = ReadShared(journal.CsvPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var records = ParseCsv(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "类别", "句柄", "现有Tag", "期望Tag", "状态", "差异字段" }, records[0]);
        Assert.Equal(result.Subject.ExistingTag, records[1][2]);
        Assert.Equal(result.Details, records[1][5]);
        Assert.Contains("中文 图纸.dwg", Encoding.UTF8.GetString(ReadShared(journal.LogPath)));
    }

    /// <summary>取消时只保留已完成两条记录，日志标明中断及剩余数量。</summary>
    [Fact]
    public void InterruptedBatchKeepsCompletedRowsAndCounts()
    {
        using var fixture = new LogFixture();
        using var journal = new TagCheckJournal(fixture.Root, "test.dwg", ["Instrument"], "无格式类别已跳过");
        int visits = 0;
        var result = TagCheckBatch.Run(Enumerable.Range(1, 5).ToArray(),
            _ => TagCheckPolicy.Check(Subject(), () => "PT-102", () => Fields()),
            () => { if (visits++ == 2) throw new OperationCanceledException(); },
            journal, (_, _) => { });
        Assert.True(result.Interrupted);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(3, result.Remaining);
        string log = Encoding.UTF8.GetString(ReadShared(journal.LogPath));
        Assert.Contains("已中断", log);
        Assert.Contains("未处理 3", log);
        Assert.Contains("不符 2", log);
    }

    /// <summary>对象错误继续处理其他对象，按五十条报告进度并包含六种状态。</summary>
    [Fact]
    public void PerObjectErrorsContinueAndProgressIsBounded()
    {
        using var fixture = new LogFixture();
        using var journal = new TagCheckJournal(fixture.Root, "test.dwg", ["Instrument"], "");
        var progress = new List<int>();
        var result = TagCheckBatch.Run(Enumerable.Range(0, 103).ToArray(),
            index => new TagCheckResult(Subject(), "", (TagCheckStatus)(index % 6), [], Error: index % 6 == 5 ? "InvalidInput" : null),
            () => { }, journal, (done, total) => { Assert.Equal(103, total); progress.Add(done); });
        Assert.Equal(new[] { 50, 100, 103 }, progress);
        Assert.Equal(103, result.Items.Count);
        Assert.Null(result.StopReason);
        string summary = TagCheckPolicy.Summary(result.Items);
        foreach (string status in new[] { "一致", "不符", "无格式", "无 Tag", "临时 Tag", "错误" })
            Assert.Contains(status, summary);
    }

    /// <summary>项目或文档身份变化属于停止，不会把后续对象伪造为检查错误。</summary>
    [Fact]
    public void ChangedContextStopsAndPreservesRemainingCount()
    {
        using var fixture = new LogFixture();
        using var journal = new TagCheckJournal(fixture.Root, "test.dwg", ["Instrument"], "");
        int calls = 0;
        var result = TagCheckBatch.Run(new[] { 1, 2, 3 },
            _ => TagCheckPolicy.Check(Subject(), () => "PT-101", () => Fields(rawNumber: "101", number: "101")),
            () => { if (calls++ == 1) throw new InvalidOperationException("项目已变化"); },
            journal, (_, _) => { });
        Assert.False(result.Interrupted);
        Assert.Equal("项目已变化", result.StopReason);
        Assert.Single(result.Items);
        Assert.Equal(2, result.Remaining);
    }

    /// <summary>失效对象保留扫描类别以计入错误汇总；已更换类别的对象不再误报一致。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("OtherClass")]
    public void BatchErrorsKeepScannedClass(string actualClass)
    {
        var item = new TagCheckResult(Subject() with { ClassName = actualClass },
            "PT-101", actualClass.Length == 0 ? TagCheckStatus.Error : TagCheckStatus.Consistent,
            [], Error: actualClass.Length == 0 ? "对象已失效" : null);
        var bound = TagCheckPolicy.BindToScannedClass(item, "PnId", "Instrument");
        Assert.Equal("Instrument", bound.Subject.ClassName);
        Assert.Equal(TagCheckStatus.Error, bound.Status);
        Assert.Contains("错误 1", TagCheckPolicy.Summary([bound]));
    }

    /// <summary>写入通道已失效时不能继续检查后续对象或虚报完成。</summary>
    [Fact]
    public void FailedLogWriteStopsBeforeNextObject()
    {
        using var fixture = new LogFixture();
        var journal = new TagCheckJournal(fixture.Root, "test.dwg", ["Instrument"], "");
        journal.Dispose();
        int calls = 0;
        Assert.Throws<ObjectDisposedException>(() => TagCheckBatch.Run(new[] { 1, 2 },
            _ => { calls++; return new(Subject(), "", TagCheckStatus.NoTag, []); },
            () => { }, journal, (_, _) => { }));
        Assert.Equal(1, calls);
    }

    /// <summary>以共享读模式核对仍打开的日志，不要求写方先释放文件。</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>独立读取 RFC 4180 记录，测试转义后的列内容而非复用生产转义函数。</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && !quoted && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row.ToArray()); row.Clear(); i++;
            }
            else field.Append(c);
        }
        Assert.False(quoted);
        Assert.Empty(row);
        Assert.Equal(0, field.Length);
        return rows;
    }

    /// <summary>只清理测试自身创建的临时目录，不接触用户项目或图纸。</summary>
    private sealed class LogFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Pn3DKit.TagCheck", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
