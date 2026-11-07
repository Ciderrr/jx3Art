using System.IO;
using System.Buffers.Binary;
namespace Int1;

// One staging bitmap + one retained latest bitmap per monitor. No worker/game APIs.
internal sealed class BitmapFrameTransport
{
    public string DirectoryPath { get; }
    public string LastSnapshotPath { get; private set; }
    private readonly string _pending;
    private bool _active;

    public BitmapFrameTransport(string parent)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(parent), "transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        _pending = Path.Combine(DirectoryPath, "staging.bmp");
    }
    internal static void Validate(byte[] data, int w, int h)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (w <= 0 || h <= 0 || data.Length != checked(w * h * 4))
            throw new ArgumentException("expected tightly packed BGRA32 bytes");
    }
    // The same codec is called by production staging and the independent decode benchmark.
    internal static void SaveBmp(byte[] data, int w, int h, string path)
    {
        Validate(data,w,h);
        // Uncompressed 32-bit BI_RGB accepts the tightly packed BGRA source.
        // A negative height keeps its top-down row order without another image
        // allocation or the WPF codec. Readers ignore the unused alpha byte.
        int bytes=checked(w*h*4),length=checked(bytes+54);
        Span<byte> header=stackalloc byte[54];header.Clear();
        header[0]=(byte)'B';header[1]=(byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..],length);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..],54);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..],40);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..],w);
        BinaryPrimitives.WriteInt32LittleEndian(header[22..],-h);
        BinaryPrimitives.WriteInt16LittleEndian(header[26..],1);
        BinaryPrimitives.WriteInt16LittleEndian(header[28..],32);
        BinaryPrimitives.WriteInt32LittleEndian(header[34..],bytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[38..],3780);
        BinaryPrimitives.WriteInt32LittleEndian(header[42..],3780);
        bool created=false;
        try
        {
            using var stream = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            created=true;
            stream.Write(header);stream.Write(data);
        }
        catch
        {
            // A CreateNew failure never grants ownership of an existing path.
            if(created)File.Delete(path);
            throw;
        }
    }
    public Lease Stage(byte[] data, int w, int h, int frameId)
    {
        Validate(data,w,h);
        if (frameId <= 0) throw new ArgumentOutOfRangeException(nameof(frameId));
        if (_active || File.Exists(_pending)) throw new IOException("frame staging already active or pending cleanup");
        // If a previous snapshot cleanup failed, do not add a third full-sized file.
        if (Directory.EnumerateFiles(DirectoryPath,"*.bmp").Take(2).Count() > 1)
            throw new IOException("snapshot cleanup blocked; refusing more frame files");
        _active = true;
        try { SaveBmp(data,w,h,_pending); return new Lease(this,frameId); }
        catch
        {
            _active = false;
            throw;
        }
    }
    internal sealed class Lease : IDisposable
    {
        private readonly BitmapFrameTransport _owner;
        private readonly int _frameId;
        private bool _closed;
        public string Path => _owner._pending;
        public Lease(BitmapFrameTransport owner,int frameId) { _owner=owner;_frameId=frameId; }
        public string KeepLatest()
        {
            if (_closed) throw new InvalidOperationException("frame lease already closed");
            string target=System.IO.Path.Combine(_owner.DirectoryPath,$"frame-{_frameId:D6}.bmp");
            string previous=_owner.LastSnapshotPath;
            File.Move(Path,target); // Never overwrite another frame's immutable filename.
            _closed=true;_owner._active=false;_owner.LastSnapshotPath=target;
            if (previous!=null) File.Delete(previous);
            return target;
        }
        public void Dispose()
        {
            if (_closed) return;
            _closed=true;_owner._active=false;
            File.Delete(Path);
        }
    }
}
