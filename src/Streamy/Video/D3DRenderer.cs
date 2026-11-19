using System.Numerics;
using System.Runtime.InteropServices;
using Streamy.Core.Geometry;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Streamy.Video;

/// <summary>
/// Draws YUV 4:2:0 frames into a window with Direct3D 11. The three planes are uploaded as R8 textures and
/// converted to RGB in a pixel shader, so the CPU never touches per-pixel colour. Runs on its own thread:
/// it wakes for a new frame or a resize, letterboxes the picture to the window and presents with a flip-model
/// swap chain.
/// </summary>
public sealed class D3DRenderer : IDisposable
{
    private const string ShaderSource = """
        Texture2D texY : register(t0);
        Texture2D texU : register(t1);
        Texture2D texV : register(t2);
        SamplerState smp : register(s0);
        cbuffer Color : register(b0)
        {
            float4 rowR;     // R = dot(rowR.xyz, yuv) + rowR.w
            float4 rowG;
            float4 rowB;
        };

        struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        // One triangle covering the viewport; no vertex buffer needed.
        VsOut vs(uint id : SV_VertexID)
        {
            VsOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float4 ps(VsOut i) : SV_Target
        {
            float3 yuv = float3(texY.Sample(smp, i.uv).r, texU.Sample(smp, i.uv).r, texV.Sample(smp, i.uv).r);
            return float4(saturate(float3(dot(rowR.xyz, yuv) + rowR.w, dot(rowG.xyz, yuv) + rowG.w, dot(rowB.xyz, yuv) + rowB.w)), 1);
        }
        """;

    private readonly IntPtr _hwnd;
    private readonly FrameExchange _frames;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private volatile bool _resizePending = true;
    private int _viewWidth, _viewHeight;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _colorBuffer;
    private readonly ID3D11Texture2D?[] _planes = new ID3D11Texture2D?[3];
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[3];
    private int _texWidth, _texHeight;
    private ColorInfo? _color;

    /// <summary>Frames drawn so far; read from any thread for an fps counter.</summary>
    public long PresentedFrames => Interlocked.Read(ref _presented);
    private long _presented;

    /// <summary>Raised on the render thread if Direct3D fails; the renderer stops.</summary>
    public event Action<Exception>? Failed;

    public D3DRenderer(IntPtr hwnd, FrameExchange frames, int width, int height)
    {
        _hwnd = hwnd;
        _frames = frames;
        _viewWidth = Math.Max(1, width);
        _viewHeight = Math.Max(1, height);
        _thread = new Thread(Run) { Name = "Streamy render", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Wake the render thread: a new frame was published.</summary>
    public void Signal() => _wake.Set();

    /// <summary>The window's client area changed size.</summary>
    public void Resize(int width, int height)
    {
        _viewWidth = Math.Max(1, width);
        _viewHeight = Math.Max(1, height);
        _resizePending = true;
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            Init();
            while (!_stop)
            {
                _wake.WaitOne(250);
                if (_stop) break;
                var frame = _frames.Take();
                bool resized = _resizePending;
                if (resized) ApplyResize();
                if (frame is not null) Upload(frame);
                if (frame is not null || resized) Draw();
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Failed?.Invoke(ex);
        }
    }

    private void Init()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device, out _context).CheckError();

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)_viewWidth,
            Height = (uint)_viewHeight,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);

        var vsCode = Compiler.Compile(ShaderSource, "vs", "streamy.hlsl", "vs_4_0");
        var psCode = Compiler.Compile(ShaderSource, "ps", "streamy.hlsl", "ps_4_0");
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _colorBuffer = _device.CreateBuffer((uint)Marshal.SizeOf<ColorMatrix>(), BindFlags.ConstantBuffer);
        CreateTargetView();
    }

    private void CreateTargetView()
    {
        using var back = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(back);
    }

    private void ApplyResize()
    {
        _resizePending = false;
        _context!.ClearState(); // releases the back buffer; Draw binds everything again
        _rtv?.Dispose();
        _swapChain!.ResizeBuffers(0, (uint)_viewWidth, (uint)_viewHeight, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTargetView();
    }

    private void Upload(VideoFrame f)
    {
        if (f.Width != _texWidth || f.Height != _texHeight)
        {
            for (int i = 0; i < 3; i++) { _views[i]?.Dispose(); _planes[i]?.Dispose(); }
            _texWidth = f.Width;
            _texHeight = f.Height;
            for (int i = 0; i < 3; i++)
            {
                int w = i == 0 ? f.Width : f.ChromaWidth, h = i == 0 ? f.Height : f.ChromaHeight;
                _planes[i] = _device!.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                _views[i] = _device.CreateShaderResourceView(_planes[i]!);
            }
        }
        _context!.UpdateSubresource<byte>(f.Y, _planes[0]!, 0, (uint)f.Width, 0);
        _context.UpdateSubresource<byte>(f.U, _planes[1]!, 0, (uint)f.ChromaWidth, 0);
        _context.UpdateSubresource<byte>(f.V, _planes[2]!, 0, (uint)f.ChromaWidth, 0);

        if (_color != f.Color)
        {
            _color = f.Color;
            var m = ColorMatrix.For(f.Color);
            _context.UpdateSubresource(in m, _colorBuffer!);
        }
    }

    private void Draw()
    {
        var ctx = _context!;
        ctx.OMSetRenderTargets(_rtv!);
        ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
        if (_texWidth > 0)
        {
            var (x, y, w, h) = new ViewMapper(_viewWidth, _viewHeight, _texWidth, _texHeight).ContentRect;
            ctx.RSSetViewport(new Viewport(x, y, w, h));
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetShaderResources(0, _views!);
            ctx.PSSetSampler(0, _sampler);
            ctx.PSSetConstantBuffer(0, _colorBuffer);
            ctx.Draw(3, 0);
        }
        _swapChain!.Present(0, PresentFlags.None);
        Interlocked.Increment(ref _presented);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        for (int i = 0; i < 3; i++) { _views[i]?.Dispose(); _planes[i]?.Dispose(); }
        _rtv?.Dispose();
        _colorBuffer?.Dispose();
        _sampler?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _wake.Dispose();
    }

    /// <summary>YUV to RGB rows for the shader: limited or full range, BT.601 or BT.709.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ColorMatrix
    {
        public Vector4 R, G, B;

        public static ColorMatrix For(ColorInfo c)
        {
            var (kr, kb) = c.Bt709 ? (0.2126f, 0.0722f) : (0.299f, 0.114f);
            float kg = 1 - kr - kb;
            // Scale and offset so that y in [0,1] and u, v centred at 0.
            float ys = c.FullRange ? 1f : 255f / 219f, yo = c.FullRange ? 0f : -16f / 255f * ys;
            float cs = c.FullRange ? 1f : 255f / 224f;
            const float co = -128f / 255f;
            float rv = 2 * (1 - kr) * cs, bu = 2 * (1 - kb) * cs;
            float gu = -bu * kb / kg, gv = -rv * kr / kg;
            return new ColorMatrix
            {
                R = new Vector4(ys, 0, rv, yo + rv * co),
                G = new Vector4(ys, gu, gv, yo + (gu + gv) * co),
                B = new Vector4(ys, bu, 0, yo + bu * co),
            };
        }
    }
}
