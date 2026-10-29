using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace ScottPlot.D3D12.Rendering;

internal sealed class D3D12SharedDevice : IDisposable
{
    private static readonly object Sync = new();
    private static D3D12SharedDevice? Instance;
    private static int Leases;

    public const int MsaaSampleCount = 4;
    public required ID3D12Device Device { get; init; }
    public required ID3D12RootSignature RootSignature { get; init; }
    public required ID3D12PipelineState PipelineState { get; init; }
    public required ID3D12PipelineState PipelineStateMsaa { get; init; }

    public static D3D12SharedDevice? Acquire()
    {
        lock (Sync)
        {
            if (Instance is null)
            {
                Instance = Create();
                if (Instance is null)
                    return null;
            }

            Leases++;
            return Instance;
        }
    }

    public void Release()
    {
        lock (Sync)
        {
            Leases--;
            if (Leases == 0)
            {
                Instance = null;
                Dispose();
            }
        }
    }

    private static D3D12SharedDevice? Create()
    {
        ID3D12Device? device = null;
        ID3D12RootSignature? rootSignature = null;
        ID3D12PipelineState? pipelineState = null;
        ID3D12PipelineState? pipelineStateMsaa = null;

        try
        {
            device = D3D12CreateDevice<ID3D12Device>(nint.Zero, FeatureLevel.Level_11_0)
                ?? throw new InvalidOperationException("D3D12 device creation failed.");

            const string shader = """
                struct CurveIn { float2 pos : POSITION; float4 col : COLOR; };
                struct CurveOut { float4 pos : SV_POSITION; float4 col : COLOR; };
                cbuffer consts : register(b0) { float4 view; };
                CurveOut VSCurve(CurveIn i)
                {
                    CurveOut o;
                    o.pos = float4(i.pos.x / view.x * 2 - 1, 1 - i.pos.y / view.y * 2, 0.5, 1);
                    o.col = i.col;
                    return o;
                }
                float4 PSCurve(CurveOut i) : SV_Target { return i.col; }
                """;

            ReadOnlyMemory<byte> vs = Compiler.Compile(shader, "VSCurve", string.Empty, "vs_5_0", ShaderFlags.None, EffectFlags.None);
            ReadOnlyMemory<byte> ps = Compiler.Compile(shader, "PSCurve", string.Empty, "ps_5_0", ShaderFlags.None, EffectFlags.None);
            RootSignatureDescription root = new(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                [new RootParameter(new RootConstants(0, 0, 4), ShaderVisibility.All)]);
            rootSignature = device.CreateRootSignature(root, RootSignatureVersion.Version10);
            GraphicsPipelineStateDescription pipelineDescription = new()
            {
                RootSignature = rootSignature,
                VertexShader = vs,
                PixelShader = ps,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = new RasterizerDescription
                {
                    FillMode = Vortice.Direct3D12.FillMode.Solid,
                    CullMode = Vortice.Direct3D12.CullMode.None,
                    FrontCounterClockwise = false,
                    DepthClipEnable = true,
                    MultisampleEnable = true,
                },
                DepthStencilState = new DepthStencilDescription
                {
                    DepthEnable = false,
                    DepthWriteMask = DepthWriteMask.All,
                    DepthFunc = ComparisonFunction.Less,
                    StencilEnable = false,
                },
                InputLayout = new InputLayoutDescription(
                [
                    new InputElementDescription { SemanticName = "POSITION", Format = Format.R32G32_Float, Slot = 0, AlignedByteOffset = 0 },
                    new InputElementDescription { SemanticName = "COLOR", SemanticIndex = 0, Format = Format.R32G32B32A32_Float, Slot = 0, AlignedByteOffset = 8 },
                ]),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            };
            pipelineState = device.CreateGraphicsPipelineState(pipelineDescription);
            pipelineDescription.SampleDescription = new SampleDescription(MsaaSampleCount, 0);
            pipelineStateMsaa = device.CreateGraphicsPipelineState(pipelineDescription);

            return new D3D12SharedDevice
            {
                Device = device,
                RootSignature = rootSignature,
                PipelineState = pipelineState,
                PipelineStateMsaa = pipelineStateMsaa,
            };
        }
        catch
        {
            pipelineStateMsaa?.Dispose();
            pipelineState?.Dispose();
            rootSignature?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        PipelineStateMsaa.Dispose();
        PipelineState.Dispose();
        RootSignature.Dispose();
        Device.Dispose();
    }
}
