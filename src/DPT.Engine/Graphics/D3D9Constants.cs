//
// DPT.Engine - Direct3D 9 constants.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// Direct3D 9 enumerations and flags.
    ///
    /// The numeric values are from d3d9.h and must not be "tidied": they are the
    /// ABI. Where a name looks redundant (D3dBlend.Zero = 1, not 0) that is the
    /// SDK's numbering, and changing it silently turns every additive particle
    /// into a black square.
    /// </summary>
    internal static class D3D9
    {
        internal const uint SdkVersion = 32;

        // ---------------------------------------------------------------- result codes

        internal const int Ok = 0;
        internal const int False = 1;
        internal const int DeviceLost = unchecked((int)0x88760868);
        internal const int DeviceNotReset = unchecked((int)0x88760869);
        internal const int NotAvailable = unchecked((int)0x8876086A);
        internal const int DriverInvalidCall = unchecked((int)0x8876086C);
        internal const int WasStillDrawing = unchecked((int)0x8876021C);

        // ---------------------------------------------------------------- device

        internal enum DeviceType : uint
        {
            Hal = 1,
            Reference = 2,
            NullReference = 3,
            Software = 5
        }

        internal static class Behavior
        {
            internal const uint SoftwareVertexProcessing = 0x00000020;
            internal const uint HardwareVertexProcessing = 0x00000040;
            internal const uint MixedVertexProcessing = 0x00000080;
            internal const uint PureDevice = 0x00000010;
            internal const uint Multithreaded = 0x00000004;
        }

        internal static class Create
        {
            internal const uint Fvf = 0x00000008;
            internal const uint AdapterGroupDevice = 0x00000200;
        }

        internal static class Present
        {
            internal const uint LockableBackBuffer = 0x00000001;
            internal const uint IntervalImmediate = 0x80000000;
            internal const uint IntervalDefault = 0;
            internal const uint IntervalOne = 1;
        }

        internal enum SwapEffect : uint
        {
            Discard = 1,
            Flip = 2,
            Copy = 3
        }

        internal enum MultiSampleType : uint
        {
            None = 0,
            NonMaskable = 1,
            TwoSamples = 2,
            ThreeSamples = 3,
            FourSamples = 4,
            SixSamples = 6,
            EightSamples = 8,
            SixteenSamples = 16
        }

        // ---------------------------------------------------------------- resources

        internal enum Pool : uint
        {
            Default = 0,
            Managed = 1,
            SystemMem = 2,
            Scratch = 3
        }

        internal static class Usage
        {
            internal const uint RenderTarget = 0x00000001;
            internal const uint DepthStencil = 0x00000002;
            internal const uint Dynamic = 0x00000200;
            internal const uint WriteOnly = 0x00000008;
            internal const uint SoftwareVertexProcessing = 0x00000040;
        }

        internal static class Lock
        {
            internal const uint ReadOnly = 0x00000010;
            internal const uint Discard = 0x00002000;
            internal const uint NoOverwrite = 0x00001000;
            internal const uint NoSysLock = 0x00002000;
            internal const uint DoNotWait = 0x00001000;
        }

        internal enum Format : uint
        {
            Unknown = 0,
            A8R8G8B8 = 21,
            X8R8G8B8 = 22,
            R5G6B5 = 23,
            A1R5G5B5 = 25,
            A4R4G4B4 = 26,
            R3G3B2 = 27,
            A8 = 28,
            X4R4G4B4 = 30,
            A2B10G10R10 = 31,
            A8B8G8R8 = 32,
            X8B8G8R8 = 33,
            A16B16G16R16 = 36,
            R16F = 111,
            G16R16F = 112,
            A16B16G16R16F = 113,
            R32F = 114,
            G32R32F = 115,
            A32B32G32R32F = 116,
            D16 = 80,
            D24X8 = 77,
            D24S8 = 75,
            D32 = 71,
            D15S1 = 73,
            Index16 = 101,
            Index32 = 102
        }

        internal enum PrimitiveType : uint
        {
            PointList = 1,
            LineList = 2,
            LineStrip = 3,
            TriangleList = 4,
            TriangleStrip = 5,
            TriangleFan = 6
        }

        // ---------------------------------------------------------------- render state

        internal static class RenderState
        {
            internal const uint ZEnable = 7;
            internal const uint FillMode = 8;
            internal const uint ShadeMode = 9;
            internal const uint ZWriteEnable = 14;
            internal const uint AlphaTestEnable = 15;
            internal const uint LastPixel = 16;
            internal const uint SrcBlend = 19;
            internal const uint DestBlend = 20;
            internal const uint CullMode = 22;
            internal const uint ZFunc = 23;
            internal const uint AlphaRef = 24;
            internal const uint AlphaFunc = 25;
            internal const uint DitherEnable = 26;
            internal const uint AlphaBlendEnable = 27;
            internal const uint FogEnable = 28;
            internal const uint SpecularEnable = 29;
            internal const uint Lighting = 137;
            internal const uint FogColor = 34;
            internal const uint FogTableMode = 35;
            internal const uint FogStart = 36;
            internal const uint FogEnd = 37;
            internal const uint FogDensity = 38;
            internal const uint RangeFogEnable = 48;
            internal const uint ZBias = 47;
            internal const uint StencilEnable = 52;
            internal const uint ScissorTestEnable = 174;
            internal const uint ColorWriteEnable = 168;
            internal const uint BlendOp = 171;
            internal const uint SrcBlendAlpha = 207;
            internal const uint DestBlendAlpha = 208;
            internal const uint BlendOpAlpha = 209;
            internal const uint SeparateAlphaBlendEnable = 206;
            internal const uint SrgbWriteEnable = 194;
            internal const uint MultisampleAntialias = 161;
        }

        internal static class Transform
        {
            internal const uint View = 2;
            internal const uint Projection = 3;
            internal const uint Texture0 = 16;
            internal const uint World = 256;
            internal const uint World1 = 257;
        }

        internal static class SamplerState
        {
            internal const uint AddressU = 1;
            internal const uint AddressV = 2;
            internal const uint AddressW = 3;
            internal const uint BorderColor = 4;
            internal const uint MagFilter = 5;
            internal const uint MinFilter = 6;
            internal const uint MipFilter = 7;
            internal const uint MipLodBias = 8;
            internal const uint MaxMipLevel = 9;
            internal const uint MaxAnisotropy = 10;
            internal const uint SrgbTexture = 11;
        }

        internal static class TextureStage
        {
            internal const uint ColorOp = 1;
            internal const uint ColorArg1 = 2;
            internal const uint ColorArg2 = 3;
            internal const uint AlphaOp = 4;
            internal const uint AlphaArg1 = 5;
            internal const uint AlphaArg2 = 6;
            internal const uint TexCoordIndex = 11;
            internal const uint TextureTransformFlags = 24;
            internal const uint ResultArg = 28;
            internal const uint Constant = 32;
        }

        internal enum TextureOp : uint
        {
            Disable = 1,
            SelectArg1 = 2,
            SelectArg2 = 3,
            Modulate = 4,
            Modulate2X = 5,
            Modulate4X = 6,
            Add = 7,
            AddSigned = 8,
            Subtract = 10,
            BlendDiffuseAlpha = 12,
            BlendTextureAlpha = 13,
            Lerp = 26,
            MultiplyAdd = 25,
            DotProduct3 = 24
        }

        internal enum TextureArgument : uint
        {
            Diffuse = 0,
            Current = 1,
            Texture = 2,
            TFactor = 3,
            Specular = 4,
            Temp = 5,
            Constant = 6
        }

        internal enum TextureAddress : uint
        {
            Wrap = 1,
            Mirror = 2,
            Clamp = 3,
            Border = 4,
            MirrorOnce = 5
        }

        internal enum TextureFilter : uint
        {
            None = 0,
            Point = 1,
            Linear = 2,
            Anisotropic = 3,
            PyramidQuad = 4,
            PointLinear = 5,
            LinearPoint = 6
        }

        /// <summary>
        /// D3DBLEND. Note that Zero is 1: the SDK reserved 0.
        ///
        /// The project format stores the GL blend factors (0, 1, 0x300..0x308),
        /// which the loader maps onto these.
        /// </summary>
        internal enum Blend : uint
        {
            Zero = 1,
            One = 2,
            SourceColor = 3,
            InverseSourceColor = 4,
            SourceAlpha = 5,
            InverseSourceAlpha = 6,
            DestinationAlpha = 7,
            InverseDestinationAlpha = 8,
            DestinationColor = 9,
            InverseDestinationColor = 10,
            SourceAlphaSaturation = 11,
            BothSourceAlpha = 12,
            BothInverseSourceAlpha = 13,
            BlendFactor = 14,
            InverseBlendFactor = 15
        }

        internal enum BlendOp : uint
        {
            Add = 1,
            Subtract = 2,
            Min = 3,
            Max = 4,
            ReverseSubtract = 5
        }

        internal enum Cull : uint
        {
            None = 1,
            Clockwise = 2,
            CounterClockwise = 3
        }

        internal enum Compare : uint
        {
            Never = 1,
            Less = 2,
            Equal = 3,
            LessEqual = 4,
            Greater = 5,
            NotEqual = 6,
            GreaterEqual = 7,
            Always = 8
        }

        internal enum Fill : uint
        {
            Point = 1,
            Wireframe = 2,
            Solid = 3
        }

        internal enum Shade : uint
        {
            Flat = 1,
            Gouraud = 2,
            Phong = 3
        }

        internal static class Clear
        {
            internal const uint Target = 0x00000001;
            internal const uint ZBuffer = 0x00000002;
            internal const uint Stencil = 0x00000004;
        }

        internal static class BackBuffer
        {
            internal const uint TypeMono = 0;
            internal const uint TypeLeft = 1;
            internal const uint TypeRight = 2;
        }

        // ---------------------------------------------------------------- FVF

        internal static class Fvf
        {
            internal const uint PositionMask = 0x00E;
            internal const uint Xyz = 0x002;
            internal const uint XyzRhw = 0x004;
            internal const uint Normal = 0x010;
            internal const uint Diffuse = 0x040;
            internal const uint Specular = 0x080;
            internal const uint Tex1 = 0x100;
            internal const uint TexCountMask = 0xF00;

            internal const uint PositionNormalTexture = Xyz | Normal | Tex1;
            internal const uint PositionNormalTextureDiffuse = Xyz | Normal | Tex1 | Diffuse;
            internal const uint PositionTexture = Xyz | Tex1;
            internal const uint PositionColor = Xyz | Diffuse;
            internal const uint PositionColorTexture = Xyz | Diffuse | Tex1;
            internal const uint PositionRhwColorTexture = XyzRhw | Diffuse | Tex1;
            internal const uint PositionRhwColor = XyzRhw | Diffuse;
        }

        // ---------------------------------------------------------------- shader registers

        /// <summary>
        /// Constant registers the engine's shaders agree on.
        ///
        /// The numbers are inherited from the original engine, where they were
        /// chosen to match the XNA SetVertexShaderConstant call sites: keeping
        /// them means the port's rendering code reads the same as the original.
        /// </summary>
        internal static class Registers
        {
            internal const int Projection = 0;
            internal const int View = 4;
            internal const int World = 8;
            internal const int TextureTransform = 12;

            internal const int PixelColor = 0;
            internal const int PixelParams1 = 1;
            internal const int PixelParams2 = 2;
        }
    }
}
