using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp=Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

public static partial class Probe
{
    private delegate bool WindowVisitor(IntPtr window,IntPtr argument);
    [DllImport("user32.dll")]private static extern bool EnumThreadWindows(uint thread,WindowVisitor visitor,IntPtr argument);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")]private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);

    /// <summary>仅关闭本测试创建且标题精确匹配的文件夹选择器；只验证CAD所有者与取消，不选择路径。</summary>
    private static string Folder(Document document,string product,string output,Action ensure,StreamWriter log)
    {
        ensure();
        IntPtr owner=AcadApp.MainWindow.Handle;
        uint uiThread=GetWindowThreadProcessId(owner,out uint process);
        string title="Pn3DKit 0.1.47 验证目录所有者 "+Guid.NewGuid().ToString("N");
        bool observed=false,owned=false;string? problem=null;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        void Poll(object? sender,EventArgs args)
        {
            EnumThreadWindows(uiThread,(window,_) =>
            {
                var text=new StringBuilder(512);GetWindowText(window,text,text.Capacity);
                if(text.ToString()!=title)return true;
                observed=true;owned=GetWindow(window,4)==owner;
                if(!owned)problem="原生文件夹对话框所有者不是CAD主窗口。";
                if(!PostMessage(window,16,IntPtr.Zero,IntPtr.Zero))problem="测试取消消息发送失败。";
                timer.Stop();return false;
            },IntPtr.Zero);
            if(watch.Elapsed>TimeSpan.FromSeconds(10)){problem="未观察到专属测试文件夹对话框。";timer.Stop();}
        }
        timer.Tick+=Poll;timer.Start();
        object? result;
        try{result=CallType(Product.GetType("Pn3DKit.UI.FolderSelectDialogue",true)!,"Select",output,owner,title);}
        finally{timer.Stop();timer.Tick-=Poll;}
        ensure();bool success=observed&&owned&&problem==null&&result==null;
        log.WriteLine(JsonSerializer.Serialize(new{success,observed,owned,problem,result}));
        return JsonSerializer.Serialize(new{Success=success,AcceptanceComplete=success,Kind="WPF目录对话框真实CAD所有者及取消",Product=product,Observed=observed,Owned=owned,Problem=problem},
            new JsonSerializerOptions{WriteIndented=true});
    }
}
