using Pn3DKit.Models;

namespace Pn3DKit.Services;

/// <summary>保存必须由用户明确确认，并在命令和撤销组全部收尾后执行。</summary>
public static class TagRebuildSave
{
    /// <summary>复用宿主的 QSAVE 完成流程，保存后仍有修改时报告失败；调用方继续持有批次租约。</summary>
    public static async Task<bool> RunAsync(bool confirmed, bool allowed, Func<Task> save,
        Func<Task<bool>> isModified, ITagRebuildJournal journal)
    {
        if (!confirmed) return false;
        if (!allowed) throw new InvalidOperationException("批次状态不明确，未提交 QSAVE。");
        journal.Message("提交 QSAVE；开始时间：" + DateTimeOffset.Now.ToString("O"));
        await save();
        if (await isModified())
            throw new InvalidOperationException("QSAVE 返回后图纸仍有修改，请检查并手动保存。");
        journal.Message("QSAVE 完成，DBMOD=0。");
        return true;
    }
}
