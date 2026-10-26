using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Pn3DKit.Interop;

/// <summary>沿用官方 FilerSample 的 CopyFiler 接口捕获类型化写出；不生成图纸文件，不执行读入。</summary>
/// <remarks>只能在 CAD 主线程由 DBObject.DwgOut 调用。未知地址、二进制及原生状态由上层明确标记不完整。</remarks>
internal sealed class XrefContentFiler(Action<string, object?> write) : DwgFiler
{
    /// <summary>保持宿主的真实错误状态，不吞掉写出失败。</summary>
    public override ErrorStatus FilerStatus { get; set; } = ErrorStatus.OK;
    /// <summary>使用 SDK 官方示例的内存复制语义。</summary>
    public override FilerType FilerType => FilerType.CopyFiler;
    /// <summary>只计类型化调用位置，不假装提供二进制文件偏移。</summary>
    public override long Position => _position;
    private long _position;
    /// <summary>每次回调保留实际类型，关联 ID 不转为数值字符串。</summary>
    private void Emit(string kind, object? value) { _position++; write(kind, value); }
    /// <summary>重置写出状态。</summary>
    public override void ResetFilerStatus() => FilerStatus = ErrorStatus.OK;
    /// <summary>内存捕获不支持寻址，调用方须将此对象标记不完整。</summary>
    public override void Seek(long offset, int method) => throw new NotSupportedException("类型化捕获不支持文件寻址。");
    /// <summary>捕获地址；上层不得比较其数值。</summary>
    public override void WriteAddress(IntPtr value) => Emit("Address", value);
    /// <summary>保留不透明字节供诊断；没有语义解释时不据此证明一致。</summary>
    public override void WriteBinaryChunk(byte[] value) => Emit("Binary", value.ToArray());
    /// <summary>捕获布尔值。</summary>
    public override void WriteBoolean(bool value) => Emit("Boolean", value);
    /// <summary>捕获字节。</summary>
    public override void WriteByte(byte value) => Emit("Byte", value);
    /// <summary>捕获不透明字节。</summary>
    public override void WriteBytes(byte[] value) => Emit("Bytes", value.ToArray());
    /// <summary>捕获原始双精度值，不使用显示精度。</summary>
    public override void WriteDouble(double value) => Emit("Double", value);
    /// <summary>句柄交给上层解析成关联，不能作为内容。</summary>
    public override void WriteHandle(Handle value) => Emit("Handle", value);
    /// <summary>捕获硬所有权关系。</summary>
    public override void WriteHardOwnershipId(ObjectId value) => Emit("HardOwner", value);
    /// <summary>捕获硬指针关系。</summary>
    public override void WriteHardPointerId(ObjectId value) => Emit("HardPointer", value);
    /// <summary>捕获短整数。</summary>
    public override void WriteInt16(short value) => Emit("Int16", value);
    /// <summary>捕获整数。</summary>
    public override void WriteInt32(int value) => Emit("Int32", value);
    /// <summary>捕获长整数。</summary>
    public override void WriteInt64(long value) => Emit("Int64", value);
    /// <summary>捕获二维点。</summary>
    public override void WritePoint2d(Point2d value) => Emit("Point2d", value);
    /// <summary>捕获三维点。</summary>
    public override void WritePoint3d(Point3d value) => Emit("Point3d", value);
    /// <summary>捕获比例。</summary>
    public override void WriteScale3d(Scale3d value) => Emit("Scale3d", value);
    /// <summary>捕获软所有权关系。</summary>
    public override void WriteSoftOwnershipId(ObjectId value) => Emit("SoftOwner", value);
    /// <summary>捕获软指针关系。</summary>
    public override void WriteSoftPointerId(ObjectId value) => Emit("SoftPointer", value);
    /// <summary>保留字符串大小写和空白。</summary>
    public override void WriteString(string value) => Emit("String", value);
    /// <summary>捕获无符号短整数。</summary>
    public override void WriteUInt16(ushort value) => Emit("UInt16", value);
    /// <summary>捕获无符号整数。</summary>
    public override void WriteUInt32(uint value) => Emit("UInt32", value);
    /// <summary>捕获无符号长整数。</summary>
    public override void WriteUInt64(ulong value) => Emit("UInt64", value);
    /// <summary>捕获二维方向。</summary>
    public override void WriteVector2d(Vector2d value) => Emit("Vector2d", value);
    /// <summary>捕获三维方向。</summary>
    public override void WriteVector3d(Vector3d value) => Emit("Vector3d", value);

    /// <summary>捕获器不可用于向原数据库读入数据。</summary>
    private static NotSupportedException Reading() => new("外参内容捕获器禁止读入操作。");
    /// <summary>禁止地址读入。</summary>
    public override IntPtr ReadAddress() => throw Reading();
    /// <summary>禁止二进制读入。</summary>
    public override byte[] ReadBinaryChunk() => throw Reading();
    /// <summary>禁止布尔读入。</summary>
    public override bool ReadBoolean() => throw Reading();
    /// <summary>禁止字节读入。</summary>
    public override byte ReadByte() => throw Reading();
    /// <summary>禁止字节数组读入。</summary>
    public override void ReadBytes(byte[] value) => throw Reading();
    /// <summary>禁止数字读入。</summary>
    public override double ReadDouble() => throw Reading();
    /// <summary>禁止句柄读入。</summary>
    public override Handle ReadHandle() => throw Reading();
    /// <summary>禁止关联读入。</summary>
    public override ObjectId ReadHardOwnershipId() => throw Reading();
    /// <summary>禁止关联读入。</summary>
    public override ObjectId ReadHardPointerId() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override short ReadInt16() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override int ReadInt32() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override long ReadInt64() => throw Reading();
    /// <summary>禁止点读入。</summary>
    public override Point2d ReadPoint2d() => throw Reading();
    /// <summary>禁止点读入。</summary>
    public override Point3d ReadPoint3d() => throw Reading();
    /// <summary>禁止比例读入。</summary>
    public override Scale3d ReadScale3d() => throw Reading();
    /// <summary>禁止关联读入。</summary>
    public override ObjectId ReadSoftOwnershipId() => throw Reading();
    /// <summary>禁止关联读入。</summary>
    public override ObjectId ReadSoftPointerId() => throw Reading();
    /// <summary>禁止文字读入。</summary>
    public override string ReadString() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override ushort ReadUInt16() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override uint ReadUInt32() => throw Reading();
    /// <summary>禁止整数读入。</summary>
    public override ulong ReadUInt64() => throw Reading();
    /// <summary>禁止方向读入。</summary>
    public override Vector2d ReadVector2d() => throw Reading();
    /// <summary>禁止方向读入。</summary>
    public override Vector3d ReadVector3d() => throw Reading();
}
