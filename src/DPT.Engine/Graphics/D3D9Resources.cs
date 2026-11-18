//
// DPT.Engine - Direct3D 9 resource wrappers.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// Shared lifetime handling for a COM object owned by the engine.
    ///
    /// Every wrapper holds exactly one reference and releases it on Dispose.
    /// Getting this wrong is the classic COM leak: one missing Release per
    /// texture per level load, and a demo that runs for eight hours at a party
    /// exhausts video memory. The release delegate is resolved once per object.
    /// </summary>
    internal abstract class ComResource : IDisposable
    {
        private readonly IntPtr handle;
        private readonly ReleaseDelegate release;
        private bool disposed;

        protected ComResource(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new ArgumentNullException("handle");

            this.handle = handle;
            release = ComVtable.Get<ReleaseDelegate>(handle, D3D9Slots.Release);
        }

        internal IntPtr Handle
        {
            get { return handle; }
        }

        internal bool IsDisposed
        {
            get { return disposed; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Dispose(true);
            release(handle);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        ~ComResource()
        {
            // A finaliser cannot safely call into COM after shutdown, but leaking
            // one object is better than a crash in the GC thread.
        }
    }

    /// <summary>A texture, either managed (uploaded once) or a default-pool render target.</summary>
    internal sealed class D3D9Texture : ComResource
    {
        private readonly int width;
        private readonly int height;
        private readonly int levels;
        private readonly bool renderTarget;
        private readonly TextureLockRectDelegate lockRect;
        private readonly TextureUnlockRectDelegate unlockRect;

        internal D3D9Texture(IntPtr handle, int width, int height, int levels, bool renderTarget)
            : base(handle)
        {
            this.width = width;
            this.height = height;
            this.levels = levels;
            this.renderTarget = renderTarget;

            lockRect = ComVtable.Get<TextureLockRectDelegate>(handle, D3D9Slots.TextureLockRect);
            unlockRect = ComVtable.Get<TextureUnlockRectDelegate>(handle, D3D9Slots.TextureUnlockRect);
        }

        internal int Width { get { return width; } }
        internal int Height { get { return height; } }
        internal int Levels { get { return levels; } }
        internal bool IsRenderTarget { get { return renderTarget; } }

        /// <summary>
        /// Uploads 32-bit BGRA pixels to one mip level.
        ///
        /// The engine's in-memory texture format is ARGB (the project format's
        /// byte order), so the caller converts; this method takes what D3D9
        /// wants, which keeps the conversion in one place at the load site rather
        /// than repeated here per level.
        /// </summary>
        internal void UploadBgra(int level, byte[] bgra)
        {
            if (bgra == null) throw new ArgumentNullException("bgra");
            if (renderTarget) throw new InvalidOperationException("Render targets cannot be uploaded to.");

            var levelWidth = Math.Max(1, width >> level);
            var expected = levelWidth * Math.Max(1, height >> level) * 4;

            if (bgra.Length != expected)
            {
                throw new ArgumentException("Expected " + expected + " bytes for level " + level +
                                            ", got " + bgra.Length + ".", "bgra");
            }

            var locked = new D3DLockedRect();
            var result = lockRect(Handle, (uint)level, ref locked, IntPtr.Zero, 0);
            ThrowIfFailed(result, "Texture.LockRect");

            try
            {
                if (locked.Bits == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Texture LockRect returned a null pointer.");
                }

                var rowBytes = levelWidth * 4;

                for (var y = 0; y < Math.Max(1, height >> level); y++)
                {
                    var destination = new IntPtr(locked.Bits.ToInt64() + (long)y * locked.Pitch);
                    Marshal.Copy(bgra, y * rowBytes, destination, rowBytes);
                }
            }
            finally
            {
                var unlockResult = unlockRect(Handle, (uint)level);
                ThrowIfFailed(unlockResult, "Texture.UnlockRect");
            }
        }

        private static void ThrowIfFailed(int result, string operation)
        {
            if (result == D3D9.Ok) return;
            if (result == D3D9.DeviceLost || result == D3D9.DeviceNotReset)
            {
                throw new D3D9Exception(operation, result);
            }

            throw new D3D9Exception(operation, result);
        }
    }

    /// <summary>A vertex buffer.</summary>
    internal sealed class D3D9VertexBuffer : ComResource
    {
        private readonly int sizeInBytes;
        private readonly LockResourceDelegate lockBuffer;
        private readonly UnlockResourceDelegate unlockBuffer;

        internal D3D9VertexBuffer(IntPtr handle, int sizeInBytes)
            : base(handle)
        {
            this.sizeInBytes = sizeInBytes;

            lockBuffer = ComVtable.Get<LockResourceDelegate>(handle, D3D9Slots.ResourceLock);
            unlockBuffer = ComVtable.Get<UnlockResourceDelegate>(handle, D3D9Slots.ResourceUnlock);
        }

        internal int SizeInBytes { get { return sizeInBytes; } }

        /// <summary>Uploads raw bytes. The caller supplies a correctly laid-out struct array.</summary>
        internal void Upload(byte[] data, uint lockFlags)
        {
            if (data == null) throw new ArgumentNullException("data");

            IntPtr locked;
            var result = lockBuffer(Handle, 0, 0, out locked, lockFlags);

            if (result != D3D9.Ok)
            {
                throw new D3D9Exception("VertexBuffer.Lock", result);
            }

            try
            {
                if (locked == IntPtr.Zero)
                {
                    throw new InvalidOperationException("VertexBuffer Lock returned a null pointer.");
                }

                Marshal.Copy(data, 0, locked, data.Length);
            }
            finally
            {
                var unlockResult = unlockBuffer(Handle);
                if (unlockResult != D3D9.Ok)
                {
                    throw new D3D9Exception("VertexBuffer.Unlock", unlockResult);
                }
            }
        }
    }

    /// <summary>An index buffer.</summary>
    internal sealed class D3D9IndexBuffer : ComResource
    {
        private readonly int sizeInBytes;
        private readonly bool thirtyTwoBit;
        private readonly LockResourceDelegate lockBuffer;
        private readonly UnlockResourceDelegate unlockBuffer;

        internal D3D9IndexBuffer(IntPtr handle, int sizeInBytes, bool thirtyTwoBit)
            : base(handle)
        {
            this.sizeInBytes = sizeInBytes;
            this.thirtyTwoBit = thirtyTwoBit;

            lockBuffer = ComVtable.Get<LockResourceDelegate>(handle, D3D9Slots.ResourceLock);
            unlockBuffer = ComVtable.Get<UnlockResourceDelegate>(handle, D3D9Slots.ResourceUnlock);
        }

        internal int SizeInBytes { get { return sizeInBytes; } }
        internal bool IsThirtyTwoBit { get { return thirtyTwoBit; } }

        internal void Upload(ushort[] indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            if (thirtyTwoBit) throw new InvalidOperationException("This is a 32-bit index buffer.");

            var bytes = new byte[indices.Length * 2];
            Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            UploadBytes(bytes);
        }

        internal void Upload(uint[] indices)
        {
            if (indices == null) throw new ArgumentNullException("indices");
            if (!thirtyTwoBit) throw new InvalidOperationException("This is a 16-bit index buffer.");

            var bytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            UploadBytes(bytes);
        }

        private void UploadBytes(byte[] data)
        {
            IntPtr locked;
            var result = lockBuffer(Handle, 0, 0, out locked, 0);

            if (result != D3D9.Ok)
            {
                throw new D3D9Exception("IndexBuffer.Lock", result);
            }

            try
            {
                if (locked == IntPtr.Zero)
                {
                    throw new InvalidOperationException("IndexBuffer Lock returned a null pointer.");
                }

                Marshal.Copy(data, 0, locked, data.Length);
            }
            finally
            {
                var unlockResult = unlockBuffer(Handle);
                if (unlockResult != D3D9.Ok)
                {
                    throw new D3D9Exception("IndexBuffer.Unlock", unlockResult);
                }
            }
        }
    }

    /// <summary>
    /// A default-pool render target.
    ///
    /// Default-pool resources live in video memory and are destroyed when the
    /// device is lost, so every render target has to be recreated on reset.
    /// </summary>
    internal sealed class D3D9RenderTarget : ComResource
    {
        private readonly int width;
        private readonly int height;

        internal D3D9RenderTarget(IntPtr handle, int width, int height)
            : base(handle)
        {
            this.width = width;
            this.height = height;
        }

        internal int Width { get { return width; } }
        internal int Height { get { return height; } }
    }

    /// <summary>A compiled vertex shader.</summary>
    internal sealed class D3D9VertexShader : ComResource
    {
        private readonly string name;

        internal D3D9VertexShader(IntPtr handle, string name)
            : base(handle)
        {
            this.name = name;
        }

        internal string Name { get { return name; } }
    }

    /// <summary>A compiled pixel shader.</summary>
    internal sealed class D3D9PixelShader : ComResource
    {
        private readonly string name;

        internal D3D9PixelShader(IntPtr handle, string name)
            : base(handle)
        {
            this.name = name;
        }

        internal string Name { get { return name; } }
    }
}
