using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp=Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

public static partial class Probe
{
    /// <summary>仅用于旧版本同路径基线，表达式与0.1.46产品一致；不进入产品载荷。</summary>
    private static string LegacyExport(string path,string marker)
    {
        string Quote(string text)=>"\""+text.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
        return "((lambda (/ r h fd cd pn) "+
            "(setq fd (getvar \"FILEDIA\") cd (getvar \"CMDDIA\") pn (getvar \"PROXYNOTICE\")) "+
            "(setvar \"FILEDIA\" 0) (setvar \"CMDDIA\" 0) (setvar \"PROXYNOTICE\" 0) "+
            "(setq r (vl-catch-all-apply 'command-s (list \"_.-EXPORTTOAUTOCAD\" \"_2018(LT2018)\" "+Quote(path)+"))) "+
            "(setvar \"FILEDIA\" fd) (setvar \"CMDDIA\" cd) (setvar \"PROXYNOTICE\" pn) "+
            "(if (setq h (open "+Quote(marker)+" \"w\")) "+
            "(progn (write-line (if (vl-catch-all-error-p r) "+
            "(strcat \"ERROR: \" (vl-catch-all-error-message r)) \"DONE\") h) (close h))) (princ)))";
    }
    /// <summary>只导出固定工具副本到独占验证路径；主线程看门狗用SDK取消停留的本次测试命令。</summary>
    private static async Task<string> PathDiagnostics(Document document,string product,string output,Action ensure,StreamWriter log)
    {
        var results=new List<object>();
        var variables=new Dictionary<string,object>();
        bool success=true;
        try
        {
            ensure();
            foreach(string name in new[]{"FILEDIA","CMDDIA","PROXYNOTICE"})
            {variables[name]=AcadApp.GetSystemVariable(name);AcadApp.SetSystemVariable(name,0);}
            foreach(var test in new[]{(133,false,false),(139,false,true)})
            {
                ensure();
                int size=test.Item1-output.Length-1-4;
                if(size<6)throw new InvalidOperationException("验证目录本身太长。");
                string stem="P"+Guid.NewGuid().ToString("N");
                string path=Path.Combine(output,(stem+new string('a',size)).Substring(0,size)+".dwg");
                if(File.Exists(path))throw new InvalidOperationException("验证目标已存在。");
                bool ended=false,canceled=false,failed=false,lispEnded=false;
                int starts=0,requests=0;
                System.Exception? error=null;
                void Started(object? sender,CommandEventArgs e)
                {if(e.GlobalCommandName.TrimStart('_','.','-').Equals("EXPORTTOAUTOCAD",StringComparison.OrdinalIgnoreCase))starts++;}
                void Ended(object? sender,CommandEventArgs e)
                {if(e.GlobalCommandName.TrimStart('_','.','-').Equals("EXPORTTOAUTOCAD",StringComparison.OrdinalIgnoreCase))ended=true;}
                void Canceled(object? sender,CommandEventArgs e){canceled=true;ended=true;}
                void Failed(object? sender,CommandEventArgs e){failed=true;ended=true;}
                void LispEnded(object? sender,EventArgs e){lispEnded=true;}
                var watch=System.Diagnostics.Stopwatch.StartNew();
                var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
                void Guard(object? sender,EventArgs e)
                {
                    try
                    {
                        ensure();
                        if(watch.Elapsed<TimeSpan.FromSeconds(5)||ended||starts==0)return;
                        if(!document.CommandInProgress.Contains("EXPORTTOAUTOCAD",StringComparison.OrdinalIgnoreCase))return;
                        if(requests++==0)log.WriteLine("SDK自动取消停留的专用路径测试："+path);
                        document.SendStringToExecute("\u0003\u0003",true,false,false);
                    }
                    catch(System.Exception ex){log.WriteLine("测试取消检查点："+ex);}
                }
                document.CommandWillStart+=Started;document.CommandEnded+=Ended;
                document.CommandCancelled+=Canceled;document.CommandFailed+=Failed;
                document.LispEnded+=LispEnded;document.LispCancelled+=LispEnded;
                timer.Tick+=Guard;timer.Start();
                log.WriteLine($"开始路径测试：长度={path.Length}；引号={test.Item2}；{path}");
                try
                {
                    if(test.Item3)
                    {
                        string marker=Path.Combine(output,"legacy-"+Guid.NewGuid().ToString("N")+".done");
                        if(File.Exists(marker))throw new InvalidOperationException("旧基线标记已存在。");
                        document.SendStringToExecute(LegacyExport(path,marker)+"\n",true,false,false);
                        await Until(()=>lispEnded&&document.Editor.IsQuiescent,ensure);
                    }
                    else await AcadApp.DocumentManager.ExecuteInCommandContextAsync(async _=>
                    {
                        try
                        {
                            ensure();
                            var args=ExportPolicy.ExportArguments(path,"2018");
                            if(test.Item2)args[2]="\""+path+"\"";
                            await document.Editor.CommandAsync(args);
                        }
                        catch(System.Exception ex){error=ex;}
                    },null);
                    await Until(()=>(test.Item3?lispEnded:ended)&&document.Editor.IsQuiescent,ensure);
                }
                finally
                {
                    timer.Stop();timer.Tick-=Guard;
                    document.CommandWillStart-=Started;document.CommandEnded-=Ended;
                    document.CommandCancelled-=Canceled;document.CommandFailed-=Failed;
                    document.LispEnded-=LispEnded;document.LispCancelled-=LispEnded;
                }
                bool written=File.Exists(path)&&new FileInfo(path).Length>0;
                if(written)ExportPolicy.ValidateOutput(path,"2018");
                results.Add(new{Length=path.Length,Quoted=test.Item2,Legacy=test.Item3,Path=path,Starts=starts,Written=written,
                    Ended=ended,Canceled=canceled,Failed=failed,CancelRequests=requests,Error=error?.ToString()});
                log.WriteLine(JsonSerializer.Serialize(results[^1]));
            }
        }
        catch(System.Exception ex){success=false;log.WriteLine(ex.ToString());}
        finally
        {
            foreach(var pair in variables)
                try{AcadApp.SetSystemVariable(pair.Key,pair.Value);}catch(System.Exception ex){success=false;log.WriteLine(ex.ToString());}
        }
        return JsonSerializer.Serialize(new{Success=success,AcceptanceComplete=false,Kind="真实路径限制诊断，不冒充最终产品验收",Product=product,Results=results},
            new JsonSerializerOptions{WriteIndented=true});
    }
}
