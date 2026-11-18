//
// DPT.Engine - COM interop for Direct3D 9.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// Vtable slot numbers and the delegates that call through them.
    ///
    /// Direct3D 9 has no CLR-friendly import library: the interfaces are COM
    /// objects, so every call goes through a vtable slot. The slots below are
    /// from d3d9.h, counted from zero and including the three IUnknown methods,
    /// which is the only numbering that matches what Marshal.ReadIntPtr finds.
    ///
    /// One deliberate difference from the naive approach: delegates are resolved
    /// once per object and cached in the owning class, not fetched per call.
    /// Resolving a delegate is a vtable read plus a managed delegate allocation;
    /// doing that for every SetRenderState in a frame is measurable in a demo
    /// that draws a few thousand primitives.
    /// </summary>
    internal static class D3D9Slots
    {
        // IUnknown
        internal const int QueryInterface = 0;
        internal const int AddRef = 1;
        internal const int Release = 2;

        // IDirect3D9
        internal const int CreateDevice = 16;
        internal const int GetDeviceCaps = 14;

        // IDirect3DDevice9
        internal const int TestCooperativeLevel = 3;
        internal const int Reset = 16;
        internal const int Present = 17;
        internal const int GetBackBuffer = 18;
        internal const int CreateTexture = 23;
        internal const int CreateVertexBuffer = 26;
        internal const int CreateIndexBuffer = 27;
        internal const int CreateRenderTarget = 28;
        internal const int CreateDepthStencilSurface = 29;
        internal const int StretchRect = 34;
        internal const int SetRenderTarget = 37;
        internal const int SetDepthStencilSurface = 39;
        internal const int BeginScene = 41;
        internal const int EndScene = 42;
        internal const int Clear = 43;
        internal const int SetTransform = 44;
        internal const int SetViewport = 47;
        internal const int SetLight = 51;
        internal const int LightEnable = 53;
        internal const int SetRenderState = 57;
        internal const int SetTexture = 65;
        internal const int SetTextureStageState = 67;
        internal const int SetSamplerState = 69;
        internal const int SetScissorRect = 75;
        internal const int DrawPrimitive = 81;
        internal const int DrawIndexedPrimitive = 82;
        internal const int DrawPrimitiveUp = 83;
        internal const int SetVertexDeclaration = 87;
        internal const int SetFvf = 89;
        internal const int CreateVertexShader = 91;
        internal const int SetVertexShader = 92;
        internal const int SetVertexShaderConstantF = 94;
        internal const int SetStreamSource = 100;
        internal const int SetIndices = 104;
        internal const int CreatePixelShader = 106;
        internal const int SetPixelShader = 107;
        internal const int SetPixelShaderConstantF = 109;
        internal const int CreateQuery = 118;

        // IDirect3DResource9-derived
        internal const int ResourceLock = 11;
        internal const int ResourceUnlock = 12;

        // IDirect3DSurface9
        internal const int SurfaceGetDesc = 12;
        internal const int SurfaceLockRect = 13;
        internal const int SurfaceUnlockRect = 14;

        // IDirect3DTexture9
        internal const int TextureLockRect = 19;
        internal const int TextureUnlockRect = 20;

        // ID3D10Blob / ID3DBlob
        internal const int BlobGetBufferPointer = 3;
        internal const int BlobGetBufferSize = 4;
    }

    /// <summary>Reads a vtable slot and turns it into a callable delegate.</summary>
    internal static class ComVtable
    {
        internal static IntPtr Method(IntPtr instance, int slot)
        {
            if (instance == IntPtr.Zero) throw new ArgumentNullException("instance");
            if (slot < 0) throw new ArgumentOutOfRangeException("slot");

            var table = Marshal.ReadIntPtr(instance);
            return Marshal.ReadIntPtr(table, checked(slot * IntPtr.Size));
        }

        internal static T Get<T>(IntPtr instance, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Method(instance, slot), typeof(T));
        }
    }

    // ------------------------------------------------------------------ delegates

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateDeviceDelegate(IntPtr d3d, uint adapter, uint deviceType, IntPtr focusWindow,
        uint behaviorFlags, ref D3DPresentParameters parameters, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDeviceCapsDelegate(IntPtr d3d, uint adapter, uint deviceType, ref D3DCaps9 caps);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint ReleaseDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int TestCooperativeLevelDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ResetDelegate(IntPtr device, ref D3DPresentParameters parameters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int PresentDelegate(IntPtr device, IntPtr sourceRect, IntPtr destRect,
        IntPtr destWindow, IntPtr dirtyRegion);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetBackBufferDelegate(IntPtr device, uint swapChain, uint backBuffer,
        uint type, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateTextureDelegate(IntPtr device, uint width, uint height, uint levels,
        uint usage, uint format, uint pool, out IntPtr texture, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateVertexBufferDelegate(IntPtr device, uint length, uint usage, uint fvf,
        uint pool, out IntPtr vertexBuffer, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateIndexBufferDelegate(IntPtr device, uint length, uint usage, uint format,
        uint pool, out IntPtr indexBuffer, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateRenderTargetDelegate(IntPtr device, uint width, uint height, uint format,
        uint multiSampleType, uint multiSampleQuality, [MarshalAs(UnmanagedType.Bool)] bool lockable,
        out IntPtr surface, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateDepthStencilSurfaceDelegate(IntPtr device, uint width, uint height, uint format,
        uint multiSampleType, uint multiSampleQuality, [MarshalAs(UnmanagedType.Bool)] bool discard,
        out IntPtr surface, IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int StretchRectDelegate(IntPtr device, IntPtr sourceSurface, ref D3DRect sourceRect,
        IntPtr destSurface, ref D3DRect destRect, uint filter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetRenderTargetDelegate(IntPtr device, uint index, IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetDepthStencilSurfaceDelegate(IntPtr device, IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int BeginSceneDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EndSceneDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ClearDelegate(IntPtr device, uint count, IntPtr rects, uint flags,
        uint color, float z, uint stencil);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetTransformDelegate(IntPtr device, uint state, ref float matrix);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetViewportDelegate(IntPtr device, ref D3DViewport viewport);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetLightDelegate(IntPtr device, uint index, ref D3DLight9 light);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int LightEnableDelegate(IntPtr device, uint index, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetRenderStateDelegate(IntPtr device, uint state, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetTextureDelegate(IntPtr device, uint stage, IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetTextureStageStateDelegate(IntPtr device, uint stage, uint type, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetSamplerStateDelegate(IntPtr device, uint sampler, uint type, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetScissorRectDelegate(IntPtr device, ref D3DRect rect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int DrawPrimitiveDelegate(IntPtr device, uint primitiveType, uint startVertex,
        uint primitiveCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int DrawIndexedPrimitiveDelegate(IntPtr device, uint primitiveType, int baseVertexIndex,
        uint minVertexIndex, uint numVertices, uint startIndex, uint primitiveCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int DrawPrimitiveUpDelegate(IntPtr device, uint primitiveType, uint primitiveCount,
        IntPtr vertexData, uint stride);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetFvfDelegate(IntPtr device, uint fvf);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateVertexShaderDelegate(IntPtr device, IntPtr function, out IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetVertexShaderDelegate(IntPtr device, IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetVertexShaderConstantFDelegate(IntPtr device, uint startRegister,
        ref float data, uint vectorCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreatePixelShaderDelegate(IntPtr device, IntPtr function, out IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetPixelShaderDelegate(IntPtr device, IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetPixelShaderConstantFDelegate(IntPtr device, uint startRegister,
        ref float data, uint vectorCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetStreamSourceDelegate(IntPtr device, uint streamNumber, IntPtr vertexBuffer,
        uint offsetInBytes, uint stride);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetIndicesDelegate(IntPtr device, IntPtr indexBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int LockResourceDelegate(IntPtr resource, uint offsetToLock, uint sizeToLock,
        out IntPtr data, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int UnlockResourceDelegate(IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int LockRectDelegate(IntPtr surface, ref D3DLockedRect lockedRect,
        IntPtr rectangle, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int UnlockRectDelegate(IntPtr surface);

    /// <summary>
    /// IDirect3DTexture9::LockRect.
    ///
    /// A different signature from the surface version despite the same name: a
    /// texture takes a mip level, so the argument list is one longer. Sharing a
    /// delegate between them corrupts the stack on x86.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int TextureLockRectDelegate(IntPtr texture, uint level, ref D3DLockedRect lockedRect,
        IntPtr rectangle, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int TextureUnlockRectDelegate(IntPtr texture, uint level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetSurfaceDescDelegate(IntPtr surface, ref D3DSurfaceDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr BlobGetPointerDelegate(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr BlobGetSizeDelegate(IntPtr blob);

    // ------------------------------------------------------------------ P/Invoke

    internal static class D3DNative
    {
        [DllImport("d3d9.dll", EntryPoint = "Direct3DCreate9", ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr Direct3DCreate9(uint sdkVersion);
    }

    /// <summary>
    /// A tiny ID3DBlob wrapper: just enough to read compiled shader bytecode.
    /// </summary>
    internal sealed class D3DBlob : IDisposable
    {
        private readonly IntPtr blob;
        private readonly BlobGetPointerDelegate getPointer;
        private readonly BlobGetSizeDelegate getSize;
        private readonly ReleaseDelegate release;

        internal D3DBlob(IntPtr blob)
        {
            this.blob = blob;
            getPointer = ComVtable.Get<BlobGetPointerDelegate>(blob, D3D9Slots.BlobGetBufferPointer);
            getSize = ComVtable.Get<BlobGetSizeDelegate>(blob, D3D9Slots.BlobGetBufferSize);
            release = ComVtable.Get<ReleaseDelegate>(blob, D3D9Slots.Release);
        }

        internal int Size
        {
            get { return (int)getSize(blob).ToInt64(); }
        }

        internal byte[] ToArray()
        {
            var size = Size;
            if (size <= 0) return new byte[0];

            var result = new byte[size];
            Marshal.Copy(getPointer(blob), result, 0, size);
            return result;
        }

        internal string ToText()
        {
            var size = Size;
            if (size <= 0) return "";

            var bytes = ToArray();
            var length = size;
            while (length > 0 && bytes[length - 1] == 0) length--;
            return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
        }

        public void Dispose()
        {
            if (blob != IntPtr.Zero) release(blob);
        }
    }
}
