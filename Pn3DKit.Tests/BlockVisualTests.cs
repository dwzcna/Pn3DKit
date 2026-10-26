using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>视觉比较离线回归；使用显式像素，不冒充真实 CAD 渲染验收。</summary>
public sealed class BlockVisualTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pn3DKit.VisualTests", Guid.NewGuid().ToString("N"));
    /// <summary>隔离每个测试的文件归属。</summary>
    public BlockVisualTests() => Directory.CreateDirectory(_root);
    /// <summary>只清理本例自建目录。</summary>
    public void Dispose() => Directory.Delete(_root, true);
    private static VisualFrame Frame => new(0, 0, 20, 2, "offline-fixture");
    private static VisualSample Sample(int no = 1) => new(no, Frame,
        [255,255,255,255, 0,0,255,255, 255,255,255,255, 255,255,255,255],
        [40,32,24,255, 0,0,255,255, 40,32,24,255, 40,32,24,255],
        [0xFFFF0000], [25], true, true, null, true, null);
    private static XrefBlockNameEntry Entry(int no) => new("Valve", new("source" + no, "A" + no + ".dwg", "fixture", no == 1, [], DwgNo: no), false);
    private static XrefBlockCandidate Candidate(params int[] numbers) => new("Valve", numbers.Select(Entry).ToArray());
    private static XrefVisualCapture Capture(int no, VisualSample? sample = null) => new(Entry(no), sample ?? Sample(no));
    private static VisualSample Pixel(VisualSample s, bool dark = false)
    {
        var pixels = (dark ? s.DarkPixels : s.LightPixels).ToArray(); pixels[4] = 91;
        return dark ? s with { DarkPixels = pixels } : s with { LightPixels = pixels };
    }

    /// <summary>同条件完整结果相同才匹配，来源编号本身不参与像素身份。</summary>
    [Fact] public void CompleteSamePixelsMatch() => Assert.Equal(VisualState.Matched, BlockVisualComparison.Compare(Sample(), Sample(2)).State);
    /// <summary>颜色图任意一个字节变化均判定不匹配，不使用比例阈值。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EitherBackgroundDifference(bool dark) => Assert.Equal(VisualState.Mismatched,
        BlockVisualComparison.Compare(Sample(), Pixel(Sample(2), dark)).State);
    /// <summary>相同截图仍核对有效颜色和透明度。</summary>
    [Theory] [InlineData(0xFF00FF00u)] [InlineData(0x7FFF0000u)]
    public void EffectiveColorAndAlphaAreCompared(uint color) => Assert.Equal(VisualState.Mismatched,
        BlockVisualComparison.Compare(Sample(), Sample(2) with { EffectiveColors = [color] }).State);
    /// <summary>截图无法辨认的细线宽差异仍必须拒绝匹配。</summary>
    [Theory] [InlineData(0)] [InlineData(30)] [InlineData(100)]
    public void EffectiveWeightIsCompared(int weight) => Assert.Equal(VisualState.Mismatched,
        BlockVisualComparison.Compare(Sample(), Sample(2) with { EffectiveLineWeights = [weight] }).State);
    /// <summary>多线宽不能仅比较取值集合；编码图位置交换即不匹配。</summary>
    [Fact] public void WeightAssignmentIsCompared()
    {
        var a = Sample() with { EffectiveLineWeights = [25, 50], WeightCodePixels = Sample().LightPixels };
        var b = Sample(2) with { EffectiveLineWeights = [25, 50], WeightCodePixels = Pixel(Sample()).LightPixels };
        Assert.Equal(VisualState.Mismatched, BlockVisualComparison.Compare(a, b).State);
    }
    /// <summary>多线宽缺少位置编码只能未验证。</summary>
    [Fact] public void MissingWeightCodeIsUnverified()
    {
        var a = Sample() with { EffectiveLineWeights = [25, 50] };
        Assert.Equal(VisualState.Unverified, BlockVisualComparison.Compare(a, a).State);
    }
    /// <summary>已确认多线宽像素及分配相同可以匹配。</summary>
    [Fact] public void EqualWeightCodeMatches()
    {
        var a = Sample() with { EffectiveLineWeights = [25, 50], WeightCodePixels = Sample().LightPixels };
        Assert.Equal(VisualState.Matched, BlockVisualComparison.Compare(a, a with { DwgNo = 2 }).State);
    }
    /// <summary>绘制失败不能用纯背景判匹配。</summary>
    [Fact] public void FailedRasterIsUnverified() => Assert.Equal(VisualState.Unverified,
        BlockVisualComparison.Compare(Sample(), Sample() with { RasterComplete = false, Problem = "异常空白" }).State);
    /// <summary>外观核对失败时同图仍未验证。</summary>
    [Fact] public void IncompleteAppearanceIsUnverified() => Assert.Equal(VisualState.Unverified,
        BlockVisualComparison.Compare(Sample(), Sample() with { AppearanceComplete = false }).State);
    /// <summary>帧的任意配置变化都不能比较。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DifferentFramesAreUnverified(int variant)
    {
        var f = variant switch { 0 => Frame with { Span = 21 }, 1 => Frame with { CenterX = 1 },
            2 => Frame with { Pixels = 3 }, _ => Frame with { ProfileId = "other" } };
        Assert.Equal(VisualState.Unverified, BlockVisualComparison.Compare(Sample(), Sample() with { Frame = f }).State);
    }
    /// <summary>无效范围及分辨率不能触发像素乘法溢出或假匹配。</summary>
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(4097)] [InlineData(int.MaxValue)]
    public void InvalidPixelsAreUnverified(int pixels)
    {
        var a = Sample() with { Frame = Frame with { Pixels = pixels } };
        Assert.Equal(VisualState.Unverified, BlockVisualComparison.Compare(a, a).State);
    }
    /// <summary>无穷或空范围拒绝比较。</summary>
    [Theory] [InlineData(0d)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidSpanIsUnverified(double span)
    {
        var a = Sample() with { Frame = Frame with { Span = span } };
        Assert.Equal(VisualState.Unverified, BlockVisualComparison.Compare(a, a).State);
    }
    /// <summary>不完整字节缓冲保持未验证。</summary>
    [Fact] public void TruncatedBufferIsUnverified() => Assert.Equal(VisualState.Unverified,
        BlockVisualComparison.Compare(Sample(), Sample() with { DarkPixels = [1] }).State);
    /// <summary>重复或无序的外观集合不能参与可靠比较。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NoncanonicalAppearanceRejected(bool color)
    {
        var a = color ? Sample() with { EffectiveColors = [3, 2] } : Sample() with { EffectiveLineWeights = [25, 25] };
        Assert.Equal(VisualState.Unverified, BlockVisualComparison.Compare(a, a).State);
    }
    /// <summary>真正空定义可以有空外观集合；是否可靠空图由宿主验证标记负责。</summary>
    [Fact] public void GenuineEmptyDefinitionMatches()
    {
        var a = Sample() with { LightPixels = new byte[16], DarkPixels = new byte[16], EffectiveColors = [], EffectiveLineWeights = [] };
        Assert.Equal(VisualState.Matched, BlockVisualComparison.Compare(a, a).State);
    }
    /// <summary>共用视窗合并全部范围，保留尺寸及相对基点的差异。</summary>
    [Fact] public void CommonFrameUsesUnion()
    {
        var frame = BlockVisualComparison.Frame([(-10d,-5d,10d,5d), (20d,-2d,24d,3d)]);
        Assert.Equal(7, frame.CenterX); Assert.Equal(0, frame.CenterY); Assert.Equal(34 * 1.15, frame.Span);
    }
    /// <summary>无范围只用于真实空定义，仍采用明确一致的视窗。</summary>
    [Fact] public void EmptyFrameIsExplicit() => Assert.Equal(1, BlockVisualComparison.Frame([]).Span);
    /// <summary>坏范围不能偷偷采用各自适配。</summary>
    [Fact] public void InvalidBoundsRejected() => Assert.Throws<ArgumentException>(() =>
        BlockVisualComparison.Frame([(double.NaN, 0, 1, 1)]));
    /// <summary>完成图像压缩后不保留调用方数组别名，解压像素完全相同。</summary>
    [Fact] public void CompressionIsLosslessAndOwnsData()
    {
        var a = Sample(); var store = new CompressedVisualSample(a); var before = store.Read();
        a.LightPixels[0] = 10; a.EffectiveColors[0] = 0;
        var after = store.Read();
        Assert.Equal(before.LightPixels, after.LightPixels); Assert.Equal(before.EffectiveColors, after.EffectiveColors);
        Assert.NotSame(before.LightPixels, after.LightPixels);
    }
    /// <summary>大面积相同像素采用压缩暂存，长期字节数低于未压缩结果。</summary>
    [Fact] public void RepresentativePixelsAreCompressed()
    {
        var a = Sample() with { Frame = Frame with { Pixels = 64 }, LightPixels = new byte[64*64*4], DarkPixels = new byte[64*64*4] };
        Assert.True(new CompressedVisualSample(a).CompressedBytes < a.LightPixels.Length);
    }
    /// <summary>A／a1／a2 的相同来源归组，只有不同组生成差异图。</summary>
    [Fact] public void ThreeSourcesAreGrouped()
    {
        var diffs = new List<VisualDifference>();
        var result = XrefBlockVisualBatch.Group(Candidate(1,2,3), [Capture(1), Capture(2, Pixel(Sample(2))), Capture(3)],
            () => true, () => {}, diffs.Add);
        Assert.Equal("不匹配", result.ResultText); Assert.True(result.Complete); Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new[] {1,3}, result.Groups[0].Entries.Select(e => e.Source.DwgNo));
        Assert.Single(diffs);
    }
    /// <summary>已知不同与未知来源并存仍不匹配，未知来源不进入组。</summary>
    [Fact] public void KnownDifferenceSurvivesUnknownSource()
    {
        var result = XrefBlockVisualBatch.Group(Candidate(1,2,3), [Capture(1), Capture(2, Pixel(Sample(2))),
            new(Entry(3), null, "缺少字体")], () => true, () => {});
        Assert.Equal("不匹配", result.ResultText); Assert.False(result.Complete);
        Assert.DoesNotContain(result.Groups.SelectMany(g => g.Entries), e => e.Source.DwgNo == 3);
        Assert.Contains(result.Problems, p => p.Contains("缺少字体"));
    }
    /// <summary>来源范围缺失时已读取相同也不能宣告全范围匹配。</summary>
    [Fact] public void ScopeIncompleteCannotMatch()
    {
        var result = XrefBlockVisualBatch.Group(Candidate(1,2), [Capture(1), Capture(2)], () => false, () => {});
        Assert.Equal("未验证", result.ResultText); Assert.True(result.HasIssues);
    }
    /// <summary>缺少候选来源的绘制交付必须留下未验证原因。</summary>
    [Fact] public void MissingCaptureIsUnverified()
    {
        var result = XrefBlockVisualBatch.Group(Candidate(1,2), [Capture(1)], () => true, () => {});
        Assert.Equal("未验证", result.ResultText);
    }
    /// <summary>所有来源无法绘制时不建立空的一致组。</summary>
    [Fact] public void AllUnknownHaveNoGroups()
    {
        var result = XrefBlockVisualBatch.Group(Candidate(1,2), [new(Entry(1),null,"动态块"),new(Entry(2),null,"代理")],
            () => true, () => {});
        Assert.Empty(result.Groups); Assert.Equal("未验证", result.ResultText);
    }
    /// <summary>同来源重复交付属于程序错误，不能制造虚假完成数。</summary>
    [Fact] public void DuplicateCaptureRejected() => Assert.Throws<InvalidOperationException>(() =>
        XrefBlockVisualBatch.Group(Candidate(1,2), [Capture(1),Capture(1)], () => true, () => {}));
    /// <summary>无候选时完全不调用绘制委托。</summary>
    [Fact] public void NoCandidateDoesNotInitializeDrawing()
    {
        int calls = 0;
        XrefBlockVisualBatch.Run([], _ => { calls++; throw new Exception(); }, () => true, () => {}, _ => {}, []);
        Assert.Equal(0, calls);
    }
    /// <summary>输出失败立即停止，不登记为已完成，也不绘制后续候选。</summary>
    [Fact] public void ReportFailureStopsBatch()
    {
        int calls = 0; var done = new List<XrefBlockReportResult>();
        Assert.Throws<IOException>(() => XrefBlockVisualBatch.Run([Candidate(1,2), Candidate(1,3)],
            _ => { calls++; return [Capture(1),Capture(2)]; }, () => true, () => {},
            _ => throw new IOException("disk"), done));
        Assert.Empty(done); Assert.Equal(1, calls);
    }
    /// <summary>取消通过检查点直接停止；剩余候选仅产生内存中的未验证行。</summary>
    [Fact] public void CancellationLeavesUnverifiedRows()
    {
        var candidate = Candidate(1,2);
        Assert.Throws<OperationCanceledException>(() => XrefBlockVisualBatch.Run([candidate],
            _ => throw new InvalidOperationException(), () => true, () => throw new OperationCanceledException(), _ => {}, []));
        var pending = XrefBlockVisualBatch.Remaining([candidate], [], "已中断").Single();
        Assert.Equal("未验证", pending.ResultText); Assert.Empty(pending.Groups);
    }
    /// <summary>设备不明状态必须停止，不能被普通来源错误吞掉。</summary>
    [Fact] public void FatalDeviceErrorStops() => Assert.Throws<XrefVisualFatalException>(() =>
        XrefBlockVisualBatch.Run([Candidate(1,2)], _ => throw new XrefVisualFatalException("device"),
            () => true, () => {}, _ => {}, []));
    /// <summary>图片只能经专用受控接口登记，普通 CreateNew 不接收任意 PNG。</summary>
    [Fact] public void OnlyNamedDiffPngCanBeOwned()
    {
        var files = new RunLogFiles();
        Assert.Throws<ArgumentException>(() => files.CreateNew(Path.Combine(_root, "user.png")));
        Assert.Throws<ArgumentException>(() => files.CreateImageReport(Path.Combine(_root, "user.png")));
        string path = Path.Combine(_root, "VisualDiff__" + Guid.NewGuid().ToString("N") + "__G1-G10.png");
        using (files.CreateImageReport(path)) {}
        files.Seal(); Assert.Equal(1, files.Delete().Deleted); Assert.False(File.Exists(path));
    }
    /// <summary>PNG 同名冲突不覆盖、不认领用户文件。</summary>
    [Fact] public void PngCreateNewConflictPreservesOriginal()
    {
        string path = Path.Combine(_root, "VisualDiff__" + Guid.NewGuid().ToString("N") + "__G1-G2.png");
        File.WriteAllText(path, "original"); var files = new RunLogFiles();
        Assert.Throws<IOException>(() => files.CreateImageReport(path));
        Assert.Empty(files.Paths); Assert.Equal("original", File.ReadAllText(path));
    }
    /// <summary>图像流未关闭时统一保护日志，不能提前清理。</summary>
    [Fact] public void OpenPngPreventsCleanup()
    {
        var files = new RunLogFiles(); string path = Path.Combine(_root, "VisualDiff__" + Guid.NewGuid().ToString("N") + "__G1-G2.png");
        using var stream = files.CreateImageReport(path); files.Seal();
        Assert.NotNull(files.ProtectionReason); Assert.Equal(0, files.Delete().Deleted);
    }
    /// <summary>像素差异图相同灰显，左右变化分别红绿，原始像素不改变。</summary>
    [Fact] public void DiffHasGrayRedAndGreenChannels()
    {
        var a = Sample(); var b = Pixel(Sample(2)); byte[] before = a.LightPixels.ToArray();
        var diff = BlockVisualDiff.Create(a,b);
        Assert.Equal(32, diff.Length); Assert.Equal(diff[0],diff[1]); Assert.Equal(diff[1],diff[2]);
        Assert.True(diff[6] > diff[5]); Assert.True(diff[13] > diff[14]);
        Assert.Equal(before,a.LightPixels);
    }
    /// <summary>PNG 实际编码可回读，归属封存后清理完整且不删除用户图片。</summary>
    [Fact] public void PngRoundtripAndUnifiedCleanup()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var files = new RunLogFiles();
                var a = Sample() with { Frame = Frame with { Pixels = 64 }, LightPixels = new byte[64*64*4], DarkPixels = new byte[64*64*4] };
                var b = a with { DwgNo = 2, LightPixels = a.LightPixels.ToArray() }; b.LightPixels[0] = 100;
                string path = VisualDifferenceReport.Write(_root, files, new("Valve",2,a,b,Entry(1),Entry(2),"仅线宽不同：25 → 30"));
                using (var input = File.OpenRead(path))
                {
                    var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(input,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    Assert.Equal(128,decoder.Frames[0].PixelWidth); Assert.Equal(184,decoder.Frames[0].PixelHeight);
                }
                string user = Path.Combine(_root,"user.png"); File.WriteAllText(user,"keep");
                files.Seal(); Assert.Equal(1,files.Delete().Deleted); Assert.Equal("keep",File.ReadAllText(user));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>PNG 故障必须标记整个报告停止，后续三列结果也不再写入。</summary>
    [Fact] public void PngFailureMarksJournalFailed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var files = new RunLogFiles();
                using var journal = new XrefBlockJournal(_root,"fixture.dwg",files);
                files.Seal();
                var a = Sample() with { Frame = Frame with { Pixels = 64 }, LightPixels = new byte[64*64*4], DarkPixels = new byte[64*64*4] };
                Assert.Throws<XrefBlockLogException>(() => journal.AppendVisualDifference(new("Valve",2,a,a with { DwgNo = 2 },Entry(1),Entry(2),"test")));
                Assert.True(journal.HasWriteFailure);
                Assert.Throws<XrefBlockLogException>(() => journal.AppendComparison(new XrefBlockReportResult("Valve",
                    XrefBlockComparisonStatus.Incomplete,false,[Entry(1),Entry(2)],[],[],["not written"])));
            }
            catch(Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>完成条件核对全部独立读回字节，截断、单字节差异或尺寸错误均拒绝。</summary>
    [Fact] public void DualReadbackRequiresAllPixels()
    {
        var frame = Frame;
        var view = Sample().LightPixels;
        Assert.True(BlockVisualComparison.CompleteReadback(frame,view,view.ToArray()));
        Assert.False(BlockVisualComparison.CompleteReadback(frame,view,Pixel(Sample()).LightPixels));
        Assert.False(BlockVisualComparison.CompleteReadback(frame,view,[]));
        Assert.False(BlockVisualComparison.CompleteReadback(frame,null,view));
        Assert.False(BlockVisualComparison.CompleteReadback(frame with { Pixels = int.MaxValue },view,view));
    }

    /// <summary>视觉 DTO 报告保留三列、每块一行及数字升序 dwgNo。</summary>
    [Fact] public void VisualReportKeepsResultSchema()
    {
        var files = new RunLogFiles();
        using (var journal = new XrefBlockJournal(_root, "fixture.dwg", files))
        {
            var entries = new[] { Entry(10),Entry(2),Entry(1),Entry(2) };
            journal.AppendComparison(new XrefBlockReportResult("Valve,\"x\"", XrefBlockComparisonStatus.Different,
                false, entries, [], ["color"], ["unknown"]));
        }
        var lines = File.ReadAllLines(Path.Combine(_root,"Result.csv"));
        Assert.Equal(2,lines.Length); Assert.Equal("块名称,dwgNo,结果",lines[0]);
        Assert.Equal("\"Valve,\"\"x\"\"\",1/2/10,不匹配",lines[1]);
    }
}
