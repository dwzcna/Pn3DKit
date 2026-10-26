using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsSystem;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using GColor = System.Drawing.Color;
using Bitmap = System.Drawing.Bitmap;
using Size = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;
using AColor = Autodesk.AutoCAD.Colors.Color;
using LineWeight = Autodesk.AutoCAD.DatabaseServices.LineWeight;
using View = Autodesk.AutoCAD.GraphicsSystem.View;
using VisualStyle = Autodesk.AutoCAD.GraphicsInterface.VisualStyle;
using VisualStyleType = Autodesk.AutoCAD.GraphicsInterface.VisualStyleType;
using VisualStyleProperty = Autodesk.AutoCAD.GraphicsInterface.VisualStyleProperty;
using VisualStyleOperation = Autodesk.AutoCAD.GraphicsInterface.VisualStyleOperation;

[assembly: CommandClass(typeof(Pn3DKit.CadVisualPrototype.Prototype))]
namespace Pn3DKit.CadVisualPrototype;

/// <summary>授权测试专用命令，不编入产品 DLL，不修改宿主图纸或宿主图形设备。</summary>
public sealed class Prototype
{
    private const string Root = @"D:\src\P3DBatchExport\artifacts\validation\0.1.34";
    private const string Authorized = Root + @"\261027003013";
    private readonly List<object> _rows = [];
    private StreamWriter? _log;

    /// <summary>在当前 CAD 主线程验证独立离屏设备、重复图像和临时块，不保存任何源图。</summary>
    [CommandMethod("PNVISUALPROTOTYPE2", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Run()
    {
        _rows.Clear();
        Directory.CreateDirectory(Root);
        string output = Path.Combine(Root, "prototype2-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(new FileStream(Path.Combine(output, "prototype.log"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _log = log;
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { log.WriteLine("未取得活动测试文档"); return; }
        var host = doc.Database;
        var originalWorking = HostApplicationServices.WorkingDatabase;
        var identity = host.BlockTableId;
        var selection = doc.Editor.SelectImplied().Value?.GetObjectIds() ?? [];
        var variables = new[] { "FILEDIA", "CMDDIA", "DBMOD", "LWDISPLAY", "TRANSPARENCYDISPLAY", "PICKFIRST", "SECURELOAD", "TRUSTEDPATHS" }
            .ToDictionary(n => n, AcadApp.GetSystemVariable);
        if (!Path.GetFullPath(host.Filename).StartsWith(Authorized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原型只允许在指定授权目录的文档中执行。");
        bool success = false;
        GraphicsKernel? kernel = null; Device? device = null; View? view = null;
        try
        {
            log.WriteLine("原型开始：" + host.Filename + "；DBMOD=" + variables["DBMOD"]);
            using var descriptor = new KernelDescriptor();
            descriptor.addRequirement(Autodesk.AutoCAD.UniqueString.Intern("3D Drawing"));
            kernel = Manager.AcquireGraphicsKernel(descriptor);
            var manager = doc.GraphicsManager;
            device = manager.CreateAutoCADOffScreenDevice(kernel);
            device.OnSize(new Size(2048, 2048));
            view = manager.CreateView(device);
            log.WriteLine("CreateView 后：attached=" + (view.Device != null) + "；visible=" + view.IsVisible
                + "；viewValid=" + view.IsValid + "；deviceValid=" + device.IsValid);
            // Device 属性不作为显示清单已注册的证明；新设备清空后明确加入唯一视图。
            device.EraseAll(); device.Add(view);
            view.Show(); view.EnableFrontClip = false; view.EnableBackClip = false;
            using var style = new VisualStyle(VisualStyleType.Wireframe2D);
            style.SetTrait(VisualStyleProperty.UseDrawOrder, true, VisualStyleOperation.Set);
            style.SetTrait(VisualStyleProperty.ViewportTransparency, true, VisualStyleOperation.Set);
            view.VisualStyle = style;
            log.WriteLine("专用设备和视图已显式注册：attached=" + (view.Device != null) + "；visible=" + view.IsVisible);
            var images = new Dictionary<string, (byte[] Light, byte[] Dark)>();
            foreach (var item in new[] { ("red", 10d, (short)1, LineWeight.LineWeight025),
                ("repeat", 10d, (short)1, LineWeight.LineWeight025),
                ("blue", 10d, (short)5, LineWeight.LineWeight025),
                ("heavy", 10d, (short)1, LineWeight.LineWeight100),
                ("larger", 12d, (short)1, LineWeight.LineWeight025) })
            {
                using var temporary = Synthetic(item.Item2, item.Item3, item.Item4, out var reference);
                images[item.Item1] = Draw(manager, kernel, device, view, temporary, reference, 0, 0, 30, output, item.Item1);
            }
            bool repeat = images["red"].Light.AsSpan().SequenceEqual(images["repeat"].Light)
                && images["red"].Dark.AsSpan().SequenceEqual(images["repeat"].Dark);
            bool color = !images["red"].Light.AsSpan().SequenceEqual(images["blue"].Light);
            bool weight = !images["red"].Light.AsSpan().SequenceEqual(images["heavy"].Light);
            bool extent = !images["red"].Light.AsSpan().SequenceEqual(images["larger"].Light);
            _rows.Add(new { Test = "synthetic-comparison", RepeatStable = repeat, ColorVisible = color,
                WeightVisible = weight, SharedFramePreservesSize = extent });
            log.WriteLine("重复稳定=" + repeat + "；颜色差异=" + color + "；线宽差异=" + weight + "；尺寸差异=" + extent);
            string sourceFile = Path.Combine(Authorized, "PID DWG", "01-原料罐.dwg");
            using var source = new Database(false, true);
            source.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
            using var sourceTx = source.TransactionManager.StartOpenCloseTransaction();
            var blocks = (BlockTable)sourceTx.GetObject(source.BlockTableId, OpenMode.ForRead);
            int captured = 0;
            foreach (ObjectId id in blocks)
            {
                var block = (BlockTableRecord)sourceTx.GetObject(id, OpenMode.ForRead);
                if (block.IsLayout || block.IsAnonymous || block.IsFromExternalReference || block.IsDynamicBlock) continue;
                try
                {
                    using var temporary = Clone(source, id, out var reference);
                    HostApplicationServices.WorkingDatabase = temporary;
                    using var tx = temporary.TransactionManager.StartOpenCloseTransaction();
                    var insert = (BlockReference)tx.GetObject(reference, OpenMode.ForRead);
                    var extents = insert.GeometricExtents;
                    double span = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, extents.MaxPoint.Y - extents.MinPoint.Y) * 1.15;
                    if (!double.IsFinite(span) || span <= 0) continue;
                    Draw(manager, kernel, device, view, temporary, reference,
                        (extents.MaxPoint.X + extents.MinPoint.X) / 2, (extents.MaxPoint.Y + extents.MinPoint.Y) / 2,
                        span, output, "real-" + captured);
                    _rows.Add(new { Test = "authorized-block", Name = block.Name, Source = sourceFile });
                    if (++captured == 3) break;
                }
                catch (System.Exception ex) { log.WriteLine("真实块原型：" + block.Name + "；" + ex); }
                finally { HostApplicationServices.WorkingDatabase = originalWorking; }
            }
            success = repeat && color && weight && extent && captured > 0;
        }
        catch (System.Exception ex) { log.WriteLine(ex); _rows.Add(new { Error = ex.ToString() }); }
        finally
        {
            HostApplicationServices.WorkingDatabase = originalWorking;
            try { view?.EraseAll(); } catch (System.Exception ex) { success = false; log.WriteLine("清理视图：" + ex); }
            try { device?.EraseAll(); } catch (System.Exception ex) { success = false; log.WriteLine("清理设备：" + ex); }
            try { view?.Dispose(); } catch (System.Exception ex) { success = false; log.WriteLine("释放视图：" + ex); }
            try { device?.Dispose(); } catch (System.Exception ex) { success = false; log.WriteLine("释放设备：" + ex); }
            try { if (kernel != null) Manager.ReleaseGraphicsKernel(kernel); }
            catch (System.Exception ex) { success = false; log.WriteLine("释放内核：" + ex); }
            var after = variables.Keys.ToDictionary(n => n, AcadApp.GetSystemVariable);
            bool stateUnchanged = variables.All(p => Equals(p.Value, after[p.Key]));
            bool selectionUnchanged = selection.ToHashSet().SetEquals(doc.Editor.SelectImplied().Value?.GetObjectIds() ?? []);
            bool documentUnchanged = AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId == identity;
            bool workingUnchanged = HostApplicationServices.WorkingDatabase == originalWorking;
            var report = new { Success = success && stateUnchanged && selectionUnchanged && documentUnchanged && workingUnchanged,
                Host = host.Filename, Before = variables, After = after, StateUnchanged = stateUnchanged,
                SelectionUnchanged = selectionUnchanged, DocumentUnchanged = documentUnchanged,
                WorkingDatabaseRestored = workingUnchanged, Tests = _rows };
            File.WriteAllText(Path.Combine(output, "prototype.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            log.WriteLine("原型结束，成功=" + report.Success);
            doc.Editor.WriteMessage("\nPN视觉原型：" + output + "；成功=" + report.Success);
        }
    }

    /// <summary>在临时数据库中建立标准块引用和可预知的圆，不注册为 CAD 文档。</summary>
    private static Database Synthetic(double radius, short aci, LineWeight weight, out ObjectId reference)
    {
        var db = new Database(true, true) { LineWeightDisplay = true };
        using var tx = db.TransactionManager.StartTransaction();
        var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForWrite);
        var definition = new BlockTableRecord { Name = "VisualPrototype" };
        table.Add(definition); tx.AddNewlyCreatedDBObject(definition, true);
        var circle = new Circle(Point3d.Origin, Vector3d.ZAxis, radius);
        circle.SetDatabaseDefaults(db); circle.ColorIndex = aci; circle.LineWeight = weight;
        definition.AppendEntity(circle); tx.AddNewlyCreatedDBObject(circle, true);
        var model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var insert = new BlockReference(Point3d.Origin, definition.ObjectId);
        insert.SetDatabaseDefaults(db); reference = model.AppendEntity(insert); tx.AddNewlyCreatedDBObject(insert, true);
        tx.Commit(); return db;
    }

    /// <summary>每个来源单独克隆，Replace 防止错误复用临时库中的默认同名样式。</summary>
    private static Database Clone(Database source, ObjectId definition, out ObjectId reference)
    {
        var db = new Database(true, true) { LineWeightDisplay = true };
        var map = new IdMapping();
        source.WblockCloneObjects(new ObjectIdCollection([definition]), db.BlockTableId, map, DuplicateRecordCloning.Replace, false);
        ObjectId clone = map[definition].Value;
        using var tx = db.TransactionManager.StartTransaction();
        var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var insert = new BlockReference(Point3d.Origin, clone); insert.SetDatabaseDefaults(db);
        reference = model.AppendEntity(insert); tx.AddNewlyCreatedDBObject(insert, true);
        var block = (BlockTableRecord)tx.GetObject(clone, OpenMode.ForRead);
        foreach (ObjectId id in block)
            if (tx.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute && !attribute.Constant)
            {
                var value = new AttributeReference();
                value.SetAttributeFromBlock(attribute, insert.BlockTransform); value.TextString = attribute.TextString;
                insert.AttributeCollection.AppendAttribute(value); tx.AddNewlyCreatedDBObject(value, true);
            }
        tx.Commit(); return db;
    }

    /// <summary>同一批复用独立设备；每个临时来源单独模型，保留双背景彩色像素。</summary>
    private (byte[] Light, byte[] Dark) Draw(Manager manager, GraphicsKernel kernel, Device device, View view,
        Database db, ObjectId reference, double x, double y, double span, string output, string name)
    {
        var previous = HostApplicationServices.WorkingDatabase;
        using var model = manager.CreateAutoCADModel(kernel);
        model.LinetypesEnabled = true;
        try
        {
            HostApplicationServices.WorkingDatabase = db;
            using var tx = db.TransactionManager.StartOpenCloseTransaction();
            var drawable = (BlockReference)tx.GetObject(reference, OpenMode.ForRead);
            var frames = new List<byte[]>();
            foreach (var background in new[] { GColor.White, GColor.FromArgb(24, 32, 40) })
            {
                view.EraseAll(); device.OnSize(new Size(2048, 2048)); device.BackgroundColor = background;
                var target = new Point3d(x, y, 0);
                view.SetView(target + Vector3d.ZAxis * span, target, Vector3d.YAxis, span, span, Projection.Parallel);
                view.ViewportExtents = new Extents2d(new Point2d(0, 0), new Point2d(1, 1));
                view.Add(drawable, model); device.Invalidate(); device.Update();
                _log!.WriteLine("设备 Update 后：device=" + device.IsValid + "；view=" + view.IsValid);
                view.Update(); view.Flush(); device.Update();
                _log.WriteLine("视图 Update/Flush 后：device=" + device.IsValid + "；view=" + view.IsValid);
                using var bitmap = view.GetSnapshot(new Rectangle(0, 0, 2048, 2048))
                    ?? throw new InvalidOperationException("离屏图像为空。");
                _log.WriteLine("Snapshot 后：device=" + device.IsValid + "；view=" + view.IsValid
                    + "；尺寸=" + bitmap.Width + "x" + bitmap.Height);
                using var normalized = new Bitmap(2048, 2048, PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(normalized)) graphics.DrawImageUnscaled(bitmap, 0, 0);
                var bits = normalized.LockBits(new Rectangle(0, 0, 2048, 2048), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var pixels = new byte[2048 * 2048 * 4];
                try { for (int row = 0; row < 2048; row++) Marshal.Copy(bits.Scan0 + row * bits.Stride, pixels, row * 2048 * 4, 2048 * 4); }
                finally { normalized.UnlockBits(bits); }
                int foreground = 0;
                for (int index = 0; index < pixels.Length; index += 4)
                    if (pixels[index] != background.B || pixels[index + 1] != background.G || pixels[index + 2] != background.R) foreground++;
                if (foreground == 0) throw new InvalidOperationException("非空圆或实际块意外得到纯背景。");
                string path = Path.Combine(output, name + (frames.Count == 0 ? "-light.png" : "-dark.png"));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) normalized.Save(file, ImageFormat.Png);
                _rows.Add(new { Test = name, Path = path, ForegroundPixels = foreground,
                    SHA256 = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant(), CenterX = x, CenterY = y, Span = span });
                _log!.WriteLine("已绘制 " + path + "；有效像素=" + foreground);
                if (!device.IsValid) throw new InvalidOperationException("已留存诊断图像，但设备绘制状态仍不完整，不能用于判定。");
                frames.Add(pixels);
            }
            return (frames[0], frames[1]);
        }
        finally { view.EraseAll(); HostApplicationServices.WorkingDatabase = previous; }
    }
}
