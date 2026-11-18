//
// DPT.Engine - Direct3D 9 interop structures.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

namespace DPT.Engine.Graphics
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DPresentParameters
    {
        internal uint BackBufferWidth;
        internal uint BackBufferHeight;
        internal uint BackBufferFormat;
        internal uint BackBufferCount;
        internal uint MultiSampleType;
        internal uint MultiSampleQuality;
        internal uint SwapEffect;
        internal IntPtr DeviceWindow;
        [MarshalAs(UnmanagedType.Bool)] internal bool Windowed;
        [MarshalAs(UnmanagedType.Bool)] internal bool EnableAutoDepthStencil;
        internal uint AutoDepthStencilFormat;
        internal uint Flags;
        internal uint FullScreenRefreshRateInHz;
        internal uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DViewport
    {
        internal uint X;
        internal uint Y;
        internal uint Width;
        internal uint Height;
        internal float MinZ;
        internal float MaxZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DLockedRect
    {
        internal int Pitch;
        internal IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DSurfaceDescription
    {
        internal uint Format;
        internal uint Type;
        internal uint Usage;
        internal uint Pool;
        internal uint MultiSampleType;
        internal uint MultiSampleQuality;
        internal uint Width;
        internal uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DColorValue
    {
        internal float R;
        internal float G;
        internal float B;
        internal float A;

        internal D3DColorValue(float r, float g, float b, float a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DVector
    {
        internal float X;
        internal float Y;
        internal float Z;

        internal D3DVector(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DLight9
    {
        internal uint Type;
        internal D3DColorValue Diffuse;
        internal D3DColorValue Specular;
        internal D3DColorValue Ambient;
        internal D3DVector Position;
        internal D3DVector Direction;
        internal float Range;
        internal float Falloff;
        internal float Attenuation0;
        internal float Attenuation1;
        internal float Attenuation2;
        internal float Theta;
        internal float Phi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DMaterial9
    {
        internal D3DColorValue Diffuse;
        internal D3DColorValue Ambient;
        internal D3DColorValue Specular;
        internal D3DColorValue Emissive;
        internal float Power;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DCaps9
    {
        internal uint DeviceType;
        internal uint AdapterOrdinal;
        internal uint Caps;
        internal uint Caps2;
        internal uint Caps3;
        internal uint PresentationIntervals;
        internal uint CursorCaps;
        internal uint DevCaps;
        internal uint PrimitiveMiscCaps;
        internal uint RasterCaps;
        internal uint ZCmpCaps;
        internal uint SrcBlendCaps;
        internal uint DestBlendCaps;
        internal uint AlphaCmpCaps;
        internal uint ShadeCaps;
        internal uint TextureCaps;
        internal uint TextureFilterCaps;
        internal uint CubeTextureFilterCaps;
        internal uint VolumeTextureFilterCaps;
        internal uint TextureAddressCaps;
        internal uint VolumeTextureAddressCaps;
        internal uint LineCaps;
        internal uint MaxTextureWidth;
        internal uint MaxTextureHeight;
        internal uint MaxVolumeExtent;
        internal uint MaxTextureRepeat;
        internal uint MaxTextureAspectRatio;
        internal uint MaxAnisotropy;
        internal float MaxVertexW;
        internal float GuardBandLeft;
        internal float GuardBandTop;
        internal float GuardBandRight;
        internal float GuardBandBottom;
        internal float ExtentsAdjust;
        internal uint StencilCaps;
        internal uint FVFCaps;
        internal uint TextureOpCaps;
        internal uint MaxTextureBlendStages;
        internal uint MaxSimultaneousTextures;
        internal uint VertexProcessingCaps;
        internal uint MaxActiveLights;
        internal uint MaxUserClipPlanes;
        internal uint MaxVertexBlendMatrices;
        internal uint MaxVertexBlendMatrixIndex;
        internal float MaxPointSize;
        internal uint MaxPrimitiveCount;
        internal uint MaxVertexIndex;
        internal uint MaxStreams;
        internal uint MaxStreamStride;
        internal uint VertexShaderVersion;
        internal uint MaxVertexShaderConst;
        internal uint PixelShaderVersion;
        internal float PixelShader1xMaxValue;
        internal uint DevCaps2;
        internal float MaxNpatchTessellationLevel;
        internal uint Reserved5;
        internal uint MasterAdapterOrdinal;
        internal uint AdapterOrdinalInGroup;
        internal uint NumberOfAdaptersInGroup;
        internal uint DeclTypes;
        internal uint NumSimultaneousRTs;
        internal uint StretchRectFilterCaps;
        internal uint VS20Caps;
        internal uint PS20Caps;
        internal uint VertexTextureFilterCaps;
        internal uint MaxVShaderInstructionsExecuted;
        internal uint MaxPShaderInstructionsExecuted;
        internal uint MaxVertexShader30InstructionSlots;
        internal uint MaxPixelShader30InstructionSlots;

        internal int VertexShaderMajor
        {
            get { return (int)((VertexShaderVersion >> 8) & 0xFF); }
        }

        internal int PixelShaderMajor
        {
            get { return (int)((PixelShaderVersion >> 8) & 0xFF); }
        }
    }

    /// <summary>
    /// A vertex carrying position, normal and one texture coordinate.
    ///
    /// This is the object vertex. The layout is fixed because it is uploaded to
    /// a managed vertex buffer with D3DFVF_XYZ | NORMAL | TEX1, and the stride
    /// passed to SetStreamSource must equal Marshal.SizeOf.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexPositionNormalTexture
    {
        internal float X, Y, Z;
        internal float NX, NY, NZ;
        internal float U, V;

        internal const uint Fvf = D3D9.Fvf.PositionNormalTexture;
        internal const int SizeInBytes = 32;

        internal VertexPositionNormalTexture(float x, float y, float z, float u, float v)
        {
            X = x; Y = y; Z = z;
            NX = 0f; NY = 0f; NZ = 0f;
            U = u; V = v;
        }
    }

    /// <summary>Position, diffuse colour and one texture coordinate: the particle vertex.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexPositionColorTexture
    {
        internal float X, Y, Z;
        internal uint Color;
        internal float U, V;

        internal const uint Fvf = D3D9.Fvf.PositionColorTexture;
        internal const int SizeInBytes = 24;
    }

    /// <summary>Position and diffuse colour: the particle tail vertex.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexPositionColor
    {
        internal float X, Y, Z;
        internal uint Color;

        internal const uint Fvf = D3D9.Fvf.PositionColor;
        internal const int SizeInBytes = 16;
    }

    /// <summary>Screen-space vertex for the 2D passes: already divided by W.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexPositionRhwColorTexture
    {
        internal float X, Y, Z;
        internal float Rhw;
        internal uint Color;
        internal float U, V;

        internal const uint Fvf = D3D9.Fvf.PositionRhwColorTexture;
        internal const int SizeInBytes = 28;
    }

    /// <summary>
    /// D3DMATRIX: sixteen floats, row-major.
    ///
    /// Kept as a plain blittable struct rather than reusing
    /// <see cref="Mathematics.Matrix"/> so the P/Invoke boundary cannot depend on
    /// field ordering in a type the rest of the engine is free to change.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DMatrix
    {
        internal float M11, M12, M13, M14;
        internal float M21, M22, M23, M24;
        internal float M31, M32, M33, M34;
        internal float M41, M42, M43, M44;

        internal static D3DMatrix Identity()
        {
            var m = new D3DMatrix();
            m.M11 = 1f;
            m.M22 = 1f;
            m.M33 = 1f;
            m.M44 = 1f;
            return m;
        }

        internal static void FromMatrix(Mathematics.Matrix source, out D3DMatrix target)
        {
            target = new D3DMatrix();
            target.M11 = source.M11; target.M12 = source.M12;
            target.M13 = source.M13; target.M14 = source.M14;
            target.M21 = source.M21; target.M22 = source.M22;
            target.M23 = source.M23; target.M24 = source.M24;
            target.M31 = source.M31; target.M32 = source.M32;
            target.M33 = source.M33; target.M34 = source.M34;
            target.M41 = source.M41; target.M42 = source.M42;
            target.M43 = source.M43; target.M44 = source.M44;
        }
    }
}
