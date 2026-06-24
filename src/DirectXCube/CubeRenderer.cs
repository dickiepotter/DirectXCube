using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace DirectXCube;

/// <summary>
/// A self-contained Direct3D 11 renderer that draws a spinning, lit cube into a Win32 window.
/// It owns the device, swap chain, depth buffer, shaders and geometry, and exposes a single
/// <see cref="Render"/> call that the host pumps once per frame.
/// </summary>
public sealed class CubeRenderer : IDisposable
{
    // One vertex of the cube: a position, the outward normal of its face (for lighting) and a colour.
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex(Vector3 position, Vector3 normal, Vector4 color)
    {
        public Vector3 Position = position;
        public Vector3 Normal = normal;
        public Vector4 Color = color;
    }

    // The constant buffer handed to the vertex/pixel shaders every frame. Laid out to match the
    // HLSL cbuffer below; 16-byte aligned (two 4x4 matrices + one float4 = 144 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 World;
        public Vector4 LightDirection;
    }

    private const int VertexStride = 40; // 3 + 3 + 4 floats

    private const string Hlsl = """
        cbuffer Constants : register(b0)
        {
            row_major float4x4 WorldViewProjection;
            row_major float4x4 World;
            float4 LightDirection;
        };

        struct VSInput  { float3 pos : POSITION; float3 nrm : NORMAL; float4 col : COLOR; };
        struct PSInput  { float4 pos : SV_POSITION; float3 nrm : NORMAL; float4 col : COLOR; };

        PSInput VS(VSInput input)
        {
            PSInput o;
            o.pos = mul(float4(input.pos, 1.0), WorldViewProjection);
            o.nrm = mul(input.nrm, (float3x3)World);
            o.col = input.col;
            return o;
        }

        float4 PS(PSInput input) : SV_TARGET
        {
            float3 n = normalize(input.nrm);
            float diffuse = saturate(dot(n, -normalize(LightDirection.xyz)));
            float intensity = saturate(0.38 + 0.8 * diffuse); // ambient + directional
            return float4(input.col.rgb * intensity, 1.0);
        }
        """;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11RenderTargetView _renderTargetView = null!;
    private ID3D11Texture2D _depthBuffer = null!;
    private ID3D11DepthStencilView _depthView = null!;
    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _pixelShader = null!;
    private ID3D11InputLayout _inputLayout = null!;
    private ID3D11Buffer _vertexBuffer = null!;
    private ID3D11Buffer _constantBuffer = null!;
    private ID3D11RasterizerState _rasterizerState = null!;

    private int _vertexCount;
    private int _width;
    private int _height;

    /// <summary>Creates the device and all GPU resources, drawing into <paramref name="hwnd"/>.</summary>
    public void Initialize(IntPtr hwnd, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
            out _device!,
            out _context!).CheckError();

        using var dxgiFactory = CreateDXGIFactory1<IDXGIFactory2>();
        var swapChainDescription = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
            AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = dxgiFactory.CreateSwapChainForHwnd(_device, hwnd, swapChainDescription);

        CreateSizedResources();
        CreatePipeline();
        CreateGeometry();
    }

    private void CreateSizedResources()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);

        _depthBuffer = _device.CreateTexture2D(new Texture2DDescription
        {
            Format = Format.D32_Float,
            Width = (uint)_width,
            Height = (uint)_height,
            ArraySize = 1,
            MipLevels = 1,
            BindFlags = BindFlags.DepthStencil,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
        });
        _depthView = _device.CreateDepthStencilView(_depthBuffer);
    }

    private void CreatePipeline()
    {
        var vsBlob = Compiler.Compile(Hlsl, "VS", "CubeRenderer.hlsl", "vs_5_0");
        var psBlob = Compiler.Compile(Hlsl, "PS", "CubeRenderer.hlsl", "ps_5_0");

        _vertexShader = _device.CreateVertexShader(vsBlob.Span);
        _pixelShader = _device.CreatePixelShader(psBlob.Span);

        _inputLayout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
        ], vsBlob.Span);

        _constantBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0));

        // No back-face culling: the depth buffer decides visibility, so winding order can't make
        // faces vanish — the classic first-cube bug the original demos never got to.
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
        });
    }

    private void CreateGeometry()
    {
        // Six faces, each a flat colour with a single outward normal. Two triangles per face.
        (Vector3 normal, Vector4 color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)[] faces =
        [
            (new(0, 0, 1), new(0.90f, 0.30f, 0.30f, 1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)),     // +Z front
            (new(0, 0, -1), new(0.30f, 0.55f, 0.90f, 1), new(1, -1, -1), new(-1, -1, -1), new(-1, 1, -1), new(1, 1, -1)), // -Z back
            (new(-1, 0, 0), new(0.95f, 0.75f, 0.25f, 1), new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, 1), new(-1, 1, -1)), // -X left
            (new(1, 0, 0), new(0.40f, 0.80f, 0.45f, 1), new(1, -1, 1), new(1, -1, -1), new(1, 1, -1), new(1, 1, 1)),      // +X right
            (new(0, 1, 0), new(0.75f, 0.45f, 0.85f, 1), new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(-1, 1, -1)),      // +Y top
            (new(0, -1, 0), new(0.85f, 0.55f, 0.35f, 1), new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1)), // -Y bottom
        ];

        var vertices = new Vertex[faces.Length * 6];
        var i = 0;
        foreach (var (normal, color, a, b, c, d) in faces)
        {
            vertices[i++] = new Vertex(a, normal, color);
            vertices[i++] = new Vertex(b, normal, color);
            vertices[i++] = new Vertex(c, normal, color);
            vertices[i++] = new Vertex(a, normal, color);
            vertices[i++] = new Vertex(c, normal, color);
            vertices[i++] = new Vertex(d, normal, color);
        }
        _vertexCount = vertices.Length;

        _vertexBuffer = _device.CreateBuffer(vertices.AsSpan(), new BufferDescription(
            (uint)(VertexStride * _vertexCount), BindFlags.VertexBuffer, ResourceUsage.Immutable,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0));
    }

    /// <summary>Resizes the swap chain and depth buffer to match the host control.</summary>
    public void Resize(int width, int height)
    {
        if (_device is null || (width == _width && height == _height))
            return;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _renderTargetView.Dispose();
        _depthView.Dispose();
        _depthBuffer.Dispose();

        _swapChain.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateSizedResources();
    }

    /// <summary>Draws one frame for the given elapsed time (seconds) and presents it.</summary>
    public void Render(float totalSeconds)
    {
        // Tumble the cube about two axes so every face comes into view.
        var world = Matrix4x4.CreateRotationY(totalSeconds * 0.9f)
                    * Matrix4x4.CreateRotationX(totalSeconds * 0.6f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 6), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, _width / (float)_height, 0.1f, 100f);

        var constants = new Constants
        {
            WorldViewProjection = world * view * projection,
            World = world,
            LightDirection = new Vector4(Vector3.Normalize(new Vector3(-0.5f, -0.6f, -0.7f)), 0),
        };
        _context.UpdateSubresource(in constants, _constantBuffer, 0, 0, 0, null);

        _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _context.RSSetState(_rasterizerState);
        _context.OMSetRenderTargets(_renderTargetView, _depthView);
        _context.ClearRenderTargetView(_renderTargetView, new Color4(0.125f, 0.157f, 0.188f, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffers(0, [_vertexBuffer], [VertexStride], [0]);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);

        _context.Draw((uint)_vertexCount, 0);
        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Copies the current back buffer to a PNG file (used to produce the README screenshot).</summary>
    public void CaptureToPng(string path)
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            Width = (uint)_width,
            Height = (uint)_height,
            ArraySize = 1,
            MipLevels = 1,
            BindFlags = BindFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(staging, backBuffer);

        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = _width * 4;
            var pixels = new byte[stride * _height];
            for (var y = 0; y < _height; y++)
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, pixels, y * stride, stride);

            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null)
                Directory.CreateDirectory(directory);
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _rasterizerState?.Dispose();
        _constantBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _depthView?.Dispose();
        _depthBuffer?.Dispose();
        _renderTargetView?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
