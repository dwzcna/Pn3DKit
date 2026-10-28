using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>提供实际修改前必须持久化的完整恢复记录。</summary>
public static class TagWriteRecovery
{
    /// <summary>记录真实目标、行身份、关联图纸、原位号及请求新位号。</summary>
    public static string Format(TagRebuildCandidate item) =>
        "准备写入：目标 " + item.Key + "；项目部分 " + item.Check.Subject.ProjectPart
        + "；" + item.Check.Subject.Identity + "；原Tag：" + item.Check.Subject.ExistingTag
        + "；新Tag：" + item.Check.ExpectedTag;
}
