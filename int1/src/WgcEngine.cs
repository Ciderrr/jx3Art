using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Int1;

/// <summary>
/// WGC 引擎——自 spk2/spk4a 已关闭验证实现复刻（SPK-2 R2 确认规则）：
/// ContentSize→FramePool.Recreate、IsCursorCaptureEnabled=false、帧池事件时间线。
/// INT-1 语义：纯被动取帧；本类不含任何窗口写调用。
/// </summary>
internal sealed class WgcEngine : IDisposable
{
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid IidID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private const int BufferCount = 2;

    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _pool;
    private GraphicsCaptureSession _session;
    private ID3D11Texture2D _staging;
    private int _stagingW, _stagingH;
    private int _arrived;
    private long _startQpc;
    private SizeInt32 _poolSize;

    public string ItemCreationPath = "";
    public bool ClosedEventSeen;
    public int RecreateCount;
    public List<string> PoolLog = new();
    public int ArrivedCount => Volatile.Read(ref _arrived);
    public SizeInt32 PoolSize => _poolSize;

    public string Start(IntPtr hwnd)
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device hw);
            if (hr.Failure || hw is null)
            {
                hr = D3D11.D3D11CreateDevice(null, DriverType.Software, DeviceCreationFlags.BgraSupport, null, out hw);
                if (hr.Failure || hw is null) return $"D3D11CreateDevice 失败: {hr}";
            }
            _device = hw;
            _context = _device.ImmediateContext;
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pDev) != 0 || pDev == IntPtr.Zero)
                return "CreateDirect3D11DeviceFromDXGIDevice 失败";
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pDev);
            Marshal.Release(pDev);

            try
            {
                _item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd));
                if (_item != null) ItemCreationPath = "windowId";
            }
            catch { }
            if (_item is null)
            {
                try
                {
                    var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
                    Guid iidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                    IntPtr pItem = interop.CreateForWindow(hwnd, ref iidItem);
                    if (pItem == IntPtr.Zero) return "CreateForWindow NULL";
                    _item = GraphicsCaptureItem.FromAbi(pItem);
                    Marshal.Release(pItem);
                    ItemCreationPath = "interop";
                }
                catch (Exception ex) { return $"CreateForWindow 异常: {ex.Message}"; }
            }
            if (_item is null) return "未能创建 GraphicsCaptureItem";

            _item.Closed += (_, _) => ClosedEventSeen = true;
            _startQpc = Stopwatch.GetTimestamp();
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, BufferCount, _item.Size);
            _pool.FrameArrived += (_, _) => Interlocked.Increment(ref _arrived);
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            _session.StartCapture();
            PoolLog.Add($"t+0ms: Item.Create(itemSize={_item.Size.Width}x{_item.Size.Height}) pool={_poolSize.Width}x{_poolSize.Height}");
            return null;
        }
        catch (Exception ex) { return $"Start 异常: {ex.GetType().Name}: {ex.Message}"; }
    }

    public sealed class PullResult
    {
        public bool Ok, FreshFrame, RecreatedThisPull;
        public string Error;
        public int SurfaceW, SurfaceH, ContentW, ContentH;
        public byte[] Bgra;
        public double FrameQpcMs;
        public FrameTiming Timing;
        public int ArrivedTotal;
        public int DrainedFrames, NoFramePolls, DiscardedFrames, InvalidFrameTimestamps;
        public bool WaitedForRefresh, RefreshDeadlineReached, FrameReadLimitReached, SourceAtOrAfterPullStart;
        public void MarkFailed(string error)
        {
            Ok = false; FreshFrame = false; Bgra = null; Timing = null; Error = error;
        }
        public double PullStartedQpcMs, SelectionStartedQpcMs, SelectionCompletedQpcMs, PixelsReadCompletedQpcMs, PullCompletedQpcMs;
    }

    public PullResult PullLatest(int maxWaitMs)
    {
        var r = new PullResult { ArrivedTotal = ArrivedCount, PullStartedQpcMs = FrameTiming.NowMs() };
        try
        {
            using var selected = FrameSelector.Select(() => _pool.TryGetNextFrame(),
                frame => frame.SystemRelativeTime.TotalMilliseconds, maxWaitMs);
            var latest = selected.Frame;
            r.SelectionStartedQpcMs = selected.StartedQpcMs;
            r.SelectionCompletedQpcMs = selected.CompletedQpcMs;
            r.DrainedFrames = selected.DequeuedFrames;
            r.NoFramePolls = selected.EmptyPolls;
            r.DiscardedFrames = selected.DiscardedFrames;
            r.InvalidFrameTimestamps = selected.InvalidTimestamps;
            r.WaitedForRefresh = selected.WaitedForRefresh;
            r.RefreshDeadlineReached = selected.DeadlineReached;
            r.FrameReadLimitReached = selected.HitReadLimit;
            r.SourceAtOrAfterPullStart = selected.SourceAtOrAfterPullStart;
            r.FreshFrame = latest != null;
            if (latest == null) { r.Error = "no-frame"; return r; }
            {
                var content = latest.ContentSize;
                r.ContentW = content.Width; r.ContentH = content.Height;
                r.FrameQpcMs = selected.SourceQpcMs;
                r.Timing = new FrameTiming(r.FrameQpcMs, selected.ReceivedQpcMs, selected.ReceivedUtc);
                ReadPixels(latest.Surface, r);
                r.PixelsReadCompletedQpcMs = FrameTiming.NowMs();
                r.ArrivedTotal = ArrivedCount;
                if (content.Width > 0 && content.Height > 0 && (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
                {
                    _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, BufferCount, content);
                    _poolSize = content;
                    RecreateCount++;
                    r.RecreatedThisPull = true;
                    PoolLog.Add($"FramePool.Recreate->{_poolSize.Width}x{_poolSize.Height}");
                }
            }
            r.Ok = r.Error == null && r.Bgra != null;
            return r;
        }
        catch (Exception ex)
        {
            r.MarkFailed($"PullLatest 异常: {ex.GetType().Name}: {ex.Message}");
            return r;
        }
        finally { r.PullCompletedQpcMs = FrameTiming.NowMs(); }
    }

    private void ReadPixels(IDirect3DSurface surface, PullResult r)
    {
        IntPtr punk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(punk);
        Guid iidTex = IidID3D11Texture2D;
        IntPtr pTex = access.GetInterface(ref iidTex);
        using var tex = new ID3D11Texture2D(pTex);
        var desc = tex.Description;
        r.SurfaceW = (int)desc.Width; r.SurfaceH = (int)desc.Height;
        if (desc.Format != Vortice.DXGI.Format.B8G8R8A8_UNorm) { r.Error = $"非预期像素格式 {desc.Format}"; return; }
        if (_staging == null || _stagingW != (int)desc.Width || _stagingH != (int)desc.Height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                Format = desc.Format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            });
            _stagingW = (int)desc.Width; _stagingH = (int)desc.Height;
        }
        _context.CopyResource(_staging, tex);
        var mapped = _context.Map(_staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            int w = (int)desc.Width, h = (int)desc.Height, rowBytes = w * 4;
            var dst = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, (int)(y * mapped.RowPitch)), dst, y * rowBytes, rowBytes);
            r.Bgra = dst;
        }
        finally { _context.Unmap(_staging, 0); }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _winrtDevice = null;
    }
}
