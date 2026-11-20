using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT;
using IDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;

namespace Streamy.Video;

/// <summary>
/// Plays a video file inside a window with Windows' own media engine (Windows.Media.Playback.MediaPlayer, so
/// HEVC, AV1 and VP9 work when Windows has those extensions, decoded on the graphics card). The player runs in
/// frame-server mode: each frame is copied into our Direct3D 11 texture and drawn letterboxed into the window's
/// swap chain. Sound plays through the default output.
/// </summary>
public sealed class VideoFilePlayer : IDisposable
{
    private const string Shader = """
        Texture2D tex : register(t0);
        SamplerState smp : register(s0);
        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VsOut vs(uint id : SV_VertexID)
        {
            VsOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }
        float4 ps(VsOut i) : SV_Target { return float4(tex.Sample(smp, i.uv).rgb, 1); }
        """;

    private readonly IntPtr _hwnd;
    private readonly Lock _gate = new();
    private readonly MediaPlayer _player = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _frame;
    private ID3D11ShaderResourceView? _frameView;
    private IDirect3DSurface? _frameSurface;
    private int _width, _height; // window
    private bool _resize;
    private bool _disposed;

    /// <summary>Raised on a worker thread when the video ends or fails (with a message).</summary>
    public event Action<string?>? Ended;

    /// <summary>Raised on a worker thread when the video is open and its length is known.</summary>
    public event Action? Opened;

    public VideoFilePlayer(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        // The media engine copies frames from its own thread while we draw from ours.
        using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
        });
        _vs = _device.CreateVertexShader(Compiler.Compile(Shader, "vs", "video.hlsl", "vs_4_0").Span);
        _ps = _device.CreatePixelShader(Compiler.Compile(Shader, "ps", "video.hlsl", "ps_4_0").Span);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        CreateTarget();

        _player.IsVideoFrameServerEnabled = true;
        _player.VideoFrameAvailable += (_, _) => OnFrame();
        _player.MediaOpened += (_, _) => Opened?.Invoke();
        _player.MediaEnded += (_, _) => Ended?.Invoke(null);
        _player.MediaFailed += (_, e) => Ended?.Invoke(string.IsNullOrEmpty(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage);
        _player.AutoPlay = true;
    }

    public void Play(string path)
    {
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public void TogglePause()
    {
        if (IsPlaying) _player.Pause(); else _player.Play();
    }

    public TimeSpan Position
    {
        get => _player.PlaybackSession.Position;
        set => _player.PlaybackSession.Position = value;
    }

    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public bool Muted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _resize = true;
        }
        // A paused video sends no frames: redraw the last one at the new size.
        Task.Run(() => { lock (_gate) { if (!_disposed) Draw(); } });
    }

    private void OnFrame()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var session = _player.PlaybackSession;
                int w = (int)session.NaturalVideoWidth, h = (int)session.NaturalVideoHeight;
                if (w <= 0 || h <= 0) return;
                EnsureFrame(w, h);
                _player.CopyFrameToVideoSurface(_frameSurface);
                Draw();
            }
            catch (COMException)
            {
                // The device was reset or the source changed mid-frame; the next frame recovers.
            }
        }
    }

    private void EnsureFrame(int w, int h)
    {
        if (_frame is not null && _frame.Description.Width == w && _frame.Description.Height == h) return;
        _frameView?.Dispose();
        _frame?.Dispose();
        _frame = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _frameView = _device.CreateShaderResourceView(_frame);
        using var dxgiSurface = _frame.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var inspectable));
        try { _frameSurface = MarshalInterface<IDirect3DSurface>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    private void Draw()
    {
        if (_resize)
        {
            _resize = false;
            _context.ClearState();
            _rtv?.Dispose();
            _swapChain.ResizeBuffers(0, (uint)_width, (uint)_height, Format.Unknown, SwapChainFlags.None).CheckError();
            CreateTarget();
        }
        _context.OMSetRenderTargets(_rtv!);
        _context.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
        if (_frame is not null && _frameView is not null)
        {
            var d = _frame.Description;
            var (x, y, w, h) = new Core.Geometry.ViewMapper(_width, _height, (int)d.Width, (int)d.Height).ContentRect;
            _context.RSSetViewport(new Viewport(x, y, w, h));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetShaderResource(0, _frameView);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
        }
        _swapChain.Present(1, PresentFlags.None);
    }

    private void CreateTarget()
    {
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(back);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _player.Pause();
        _player.Source = null;
        _player.Dispose();
        lock (_gate)
        {
            _frameView?.Dispose();
            _frame?.Dispose();
            _rtv?.Dispose();
            _sampler.Dispose();
            _ps.Dispose();
            _vs.Dispose();
            _swapChain.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);
}
