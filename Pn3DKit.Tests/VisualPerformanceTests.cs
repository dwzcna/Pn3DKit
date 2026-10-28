using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证优化保留完整核对，并以调用量衡量收益，不冒充实际CAD耗时。</summary>
public sealed class VisualPerformanceTests
{
    private static XrefBlockNameEntry Entry(int no) => new("Valve",new("s"+no,no+".dwg","fixture",no==1,[],DwgNo:no),false);
    private static VisualSample Sample(int no, byte color=0) => new(no,new(0,0,1,1,"fixture"),
        [color,0,255,255],[color,0,255,255],[0xFFFF0000],[25],true,true,null,true,null);

    /// <summary>大量匹配来源只压缩一个代表，不分配整张解压代表。</summary>
    [Fact]
    public void MatchingSourcesOnlyCompressRepresentative()
    {
        var candidate = new XrefBlockCandidate("Valve",Enumerable.Range(1,100).Select(Entry).ToArray());
        var metrics = new VisualBatchMetrics();
        var result = XrefBlockVisualBatch.Group(candidate,candidate.Entries.Select(e => new XrefVisualCapture(e,Sample(e.Source.DwgNo))),
            () => true,() => {},metrics:metrics);
        Assert.Equal("匹配",result.ResultText); Assert.Equal(100,metrics.Fingerprints);
        Assert.Equal(1,metrics.Representatives); Assert.Equal(99,metrics.PixelComparisons);
        Assert.Equal(0,metrics.ExpandedRepresentatives);
    }

    /// <summary>强制摘要碰撞仍完整核对像素，不把摘要作为一致证据。</summary>
    [Fact]
    public void HashCollisionStillChecksAllPixels()
    {
        var candidate = new XrefBlockCandidate("Valve",[Entry(1),Entry(2),Entry(3)]);
        var metrics = new VisualBatchMetrics();
        var result = XrefBlockVisualBatch.Group(candidate,[new(Entry(1),Sample(1)),new(Entry(2),Sample(2,4)),new(Entry(3),Sample(3))],
            () => true,() => {},metrics:metrics,fingerprint:_ => "collision");
        Assert.Equal("不匹配",result.ResultText);
        Assert.Equal(2,metrics.Representatives); Assert.Equal(1,metrics.ExpandedRepresentatives);
        Assert.Equal(new[]{1,3},result.Groups[0].Entries.Select(e=>e.Source.DwgNo));
    }

    /// <summary>二维筛选避免同X或同Y但另一轴离散时退化为全部配对。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void DisjointBoxesAvoidQuadraticPairs(bool vertical)
    {
        const int count=10000;
        var boxes=Enumerable.Range(0,count).Select(i => vertical
            ? new VisualDisplayBox(0,i*10,1,i*10+1,(uint)(i%2),25)
            : new VisualDisplayBox(i*10,0,i*10+1,1,(uint)(i%2),25)).ToArray();
        var metrics=new VisualOverlapMetrics();
        Assert.False(VisualOverlapIndex.HasOverlap(boxes,0,()=>{},metrics));
        Assert.True(metrics.Pairs<count*16L,$"pairs={metrics.Pairs}");
    }

    /// <summary>确定性随机输入与原双循环规则逐组对照，包括边界接触、外观和margin。</summary>
    [Theory] [InlineData(0.0)] [InlineData(0.05)]
    public void SpatialResultsMatchBruteForce(double margin)
    {
        var random=new Random(41);
        for(int trial=0;trial<100;trial++)
        {
            var boxes=Enumerable.Range(0,40).Select(_=>{
                double x=random.Next(100),y=random.Next(100);
                return new VisualDisplayBox(x,y,x+random.Next(4),y+random.Next(4),(uint)random.Next(3),random.Next(2));
            }).ToArray();
            bool expected=false;
            for(int i=0;i<boxes.Length;i++) for(int j=i+1;j<boxes.Length;j++)
            {
                var a=boxes[i];var b=boxes[j];
                if((a.Color!=b.Color||a.Weight!=b.Weight)&&b.MinX<=a.MaxX+margin&&b.MaxX>=a.MinX-margin
                    &&b.MinY<=a.MaxY+margin&&b.MaxY>=a.MinY-margin)expected=true;
            }
            Assert.Equal(expected,VisualOverlapIndex.HasOverlap(boxes,margin,()=>{}));
        }
    }

    /// <summary>构建及长查询均检查取消，不能用取消结果判为无重叠。</summary>
    [Fact]
    public void CancellationPropagates()
    {
        var boxes=Enumerable.Range(0,1000).Select(i=>new VisualDisplayBox(0,i,1,i+0.5,(uint)(i%2),25)).ToArray();
        Assert.Throws<OperationCanceledException>(()=>VisualOverlapIndex.HasOverlap(boxes,0,()=>throw new OperationCanceledException()));
    }
}
