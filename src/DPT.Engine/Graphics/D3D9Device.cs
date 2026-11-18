//
// DPT.Engine - Direct3D 9 device.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Mathematics;

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// The engine's Direct3D 9 device.
    ///
    /// This is the only class that talks to the driver. Everything above it -
    /// the renderer, the events, the particle system - works in engine types
    /// (<see cref="Matrix"/>, <see cref="ColorRgba"/>) and never touches a COM
    /// pointer.
    ///
    /// Three decisions shape the file.
    ///
    /// * Delegates are resolved once, at creation, and cached. A vtable read plus
    ///   a delegate allocation per SetRenderState call is measurable when a frame
    ///   issues thousands of them.
    ///
    /// * Render state is cached. Setting a state to the value it already has is
    ///   skipped, because the driver would otherwise re-validate the pipeline for
    ///   nothing. The cache is flushed on Reset, where the driver forgets
    ///   everything.
    ///
    /// * Device loss is normal. Alt-tab, a driver update or a resolution change
    ///   loses the device; <see cref="TestCooperativeLevel"/> reports it and
    ///   <see cref="Reset"/> recovers. Nothing here throws on DeviceLost - it
    ///   sets a flag and returns, because a lost device is not a bug in the
    ///   engine and must not reach the fault guard as one.
    /// </summary>
    internal sealed class D3D9Device : IDisposable
    {
        private IntPtr d3d;
        private IntPtr device;
        private IntPtr window;
        private IntPtr backBuffer;
        private IntPtr depthStencil;
        private IntPtr readbackSurface;
        private int readbackWidth;
        private int readbackHeight;
        private D3D9.Format readbackFormat;
        private volatile bool readbackDisabled;
        private IntPtr targetDepth;
        private int targetDepthWidth;
        private int targetDepthHeight;

        private int width;
        private int height;
        private bool windowed;
        private bool vsync;
        private bool sceneOpen;
        private bool lost;
        private bool disposed;

        private string description = "";
        private D3DCaps9 caps;

        /// <summary>Cached render state, indexed by state id (0..255 is enough for D3D9).</summary>
        private readonly uint[] stateCache = new uint[256];
        private readonly bool[] stateSet = new bool[256];

        private readonly uint[] samplerCache = new uint[4 * 16];
        private readonly bool[] samplerSet = new bool[4 * 16];

        private readonly uint[] textureStageCache = new uint[8 * 40];
        private readonly bool[] textureStageSet = new bool[8 * 40];

        private IntPtr currentTexture0 = IntPtr.Zero;
        private IntPtr currentTexture1 = IntPtr.Zero;
        private IntPtr currentVertexShader = IntPtr.Zero;
        private IntPtr currentPixelShader = IntPtr.Zero;
        private uint currentFvf = uint.MaxValue;
        private IntPtr currentStream0 = IntPtr.Zero;
        private IntPtr currentIndices = IntPtr.Zero;

        // ---------------------------------------------------------------- delegates

        private ReleaseDelegate release;
        private TestCooperativeLevelDelegate testCooperativeLevel;
        private ResetDelegate reset;
        private PresentDelegate present;
        private GetBackBufferDelegate getBackBuffer;
        private CreateTextureDelegate createTexture;
        private CreateVertexBufferDelegate createVertexBuffer;
        private CreateIndexBufferDelegate createIndexBuffer;
        private CreateRenderTargetDelegate createRenderTarget;
        private CreateDepthStencilSurfaceDelegate createDepthStencil;
        private StretchRectDelegate stretchRect;
        private SetRenderTargetDelegate setRenderTarget;
        private SetDepthStencilSurfaceDelegate setDepthStencilSurface;
        private BeginSceneDelegate beginScene;
        private EndSceneDelegate endScene;
        private ClearDelegate clear;
        private SetTransformDelegate setTransform;
        private SetViewportDelegate setViewport;
        private SetLightDelegate setLight;
        private LightEnableDelegate lightEnable;
        private SetRenderStateDelegate setRenderState;
        private SetTextureDelegate setTexture;
        private SetTextureStageStateDelegate setTextureStageState;
        private SetSamplerStateDelegate setSamplerState;
        private SetScissorRectDelegate setScissorRect;
        private DrawPrimitiveDelegate drawPrimitive;
        private DrawIndexedPrimitiveDelegate drawIndexedPrimitive;
        private DrawPrimitiveUpDelegate drawPrimitiveUp;
        private SetFvfDelegate setFvf;
        private CreateVertexShaderDelegate createVertexShader;
        private SetVertexShaderDelegate setVertexShader;
        private SetVertexShaderConstantFDelegate setVertexShaderConstantF;
        private SetStreamSourceDelegate setStreamSource;
        private SetIndicesDelegate setIndices;
        private CreatePixelShaderDelegate createPixelShader;
        private SetPixelShaderDelegate setPixelShader;
        private SetPixelShaderConstantFDelegate setPixelShaderConstantF;

        // ---------------------------------------------------------------- properties

        internal IntPtr Handle { get { return device; } }
        internal int Width { get { return width; } }
        internal int Height { get { return height; } }
        internal bool IsWindowed { get { return windowed; } }
        internal bool IsLost { get { return lost; } }
        internal string Description { get { return description; } }
        internal D3DCaps9 Caps { get { return caps; } }

        internal bool SupportsPixelShaders
        {
            get { return caps.PixelShaderMajor >= 2; }
        }

        internal bool SupportsVertexShaders
        {
            get { return caps.VertexShaderMajor >= 2; }
        }

        // ---------------------------------------------------------------- creation

        /// <summary>
        /// Creates the device for a window.
        ///
        /// Tries hardware vertex processing first, then mixed, then software, then
        /// the reference rasteriser. Each step down is a real fallback for a real
        /// machine: a broken vertex-shader driver, a very old chipset, or a
        /// virtual machine with no 3D at all. The chosen mode is reported through
        /// <see cref="Description"/> so the launcher can say which one is in use.
        /// </summary>
        internal void Create(IntPtr windowHandle, int backBufferWidth, int backBufferHeight,
            bool fullScreen, bool enableVsync)
        {
            if (disposed) throw new ObjectDisposedException("D3D9Device");
            if (windowHandle == IntPtr.Zero) throw new ArgumentNullException("windowHandle");
            if (device != IntPtr.Zero) throw new InvalidOperationException("The device is already created.");
            if (backBufferWidth <= 0) throw new ArgumentOutOfRangeException("backBufferWidth");
            if (backBufferHeight <= 0) throw new ArgumentOutOfRangeException("backBufferHeight");

            window = windowHandle;
            width = backBufferWidth;
            height = backBufferHeight;
            windowed = !fullScreen;
            vsync = enableVsync;

            d3d = D3DNative.Direct3DCreate9(D3D9.SdkVersion);

            if (d3d == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Direct3DCreate9 returned null: Direct3D 9 is not installed on this machine.");
            }

            var create = ComVtable.Get<CreateDeviceDelegate>(d3d, D3D9Slots.CreateDevice);

            // Ordered from fastest to most compatible. The reference device is last
            // because it is a software rasteriser and is slow enough to be a
            // diagnostic tool rather than a way to play.
            var attempts = new[]
            {
                new DeviceAttempt("HAL, hardware vertex processing", D3D9.DeviceType.Hal,
                    D3D9.Behavior.HardwareVertexProcessing),
                new DeviceAttempt("HAL, mixed vertex processing", D3D9.DeviceType.Hal,
                    D3D9.Behavior.MixedVertexProcessing),
                new DeviceAttempt("HAL, software vertex processing", D3D9.DeviceType.Hal,
                    D3D9.Behavior.SoftwareVertexProcessing),
                new DeviceAttempt("REF, software vertex processing", D3D9.DeviceType.Reference,
                    D3D9.Behavior.SoftwareVertexProcessing)
            };

            var lastResult = 0;

            for (var i = 0; i < attempts.Length; i++)
            {
                var attempt = attempts[i];
                var parameters = BuildPresentParameters(attempt.Behavior);

                IntPtr created;
                var result = create(d3d, 0, (uint)attempt.Type, window, attempt.Behavior,
                    ref parameters, out created);

                lastResult = result;

                if (result == D3D9.Ok && created != IntPtr.Zero)
                {
                    device = created;
                    description = attempt.Name;
                    EngineLog.Info("graphics", "Device created: " + description + ".");
                    break;
                }

                if (created != IntPtr.Zero)
                {
                    ComVtable.Get<ReleaseDelegate>(created, D3D9Slots.Release)(created);
                }

                EngineLog.Verbose("graphics", "Device attempt failed (" + attempt.Name + "): 0x" +
                                              result.ToString("X8"));
            }

            if (device == IntPtr.Zero)
            {
                ReleaseD3D();

                throw new InvalidOperationException(
                    "IDirect3D9::CreateDevice failed for every device type (last result 0x" +
                    lastResult.ToString("X8") + "). No Direct3D 9 capable display driver was found.");
            }

            CacheDelegates();
            ReadCaps();
            InvalidateState();
            AcquireBackBuffer();
            AcquireDepthStencil();
        }

        private struct DeviceAttempt
        {
            internal readonly string Name;
            internal readonly D3D9.DeviceType Type;
            internal readonly uint Behavior;

            internal DeviceAttempt(string name, D3D9.DeviceType type, uint behavior)
            {
                Name = name;
                Type = type;
                Behavior = behavior;
            }
        }

        private void CacheDelegates()
        {
            release = ComVtable.Get<ReleaseDelegate>(device, D3D9Slots.Release);
            testCooperativeLevel = ComVtable.Get<TestCooperativeLevelDelegate>(device, D3D9Slots.TestCooperativeLevel);
            reset = ComVtable.Get<ResetDelegate>(device, D3D9Slots.Reset);
            present = ComVtable.Get<PresentDelegate>(device, D3D9Slots.Present);
            getBackBuffer = ComVtable.Get<GetBackBufferDelegate>(device, D3D9Slots.GetBackBuffer);
            createTexture = ComVtable.Get<CreateTextureDelegate>(device, D3D9Slots.CreateTexture);
            createVertexBuffer = ComVtable.Get<CreateVertexBufferDelegate>(device, D3D9Slots.CreateVertexBuffer);
            createIndexBuffer = ComVtable.Get<CreateIndexBufferDelegate>(device, D3D9Slots.CreateIndexBuffer);
            createRenderTarget = ComVtable.Get<CreateRenderTargetDelegate>(device, D3D9Slots.CreateRenderTarget);
            createDepthStencil = ComVtable.Get<CreateDepthStencilSurfaceDelegate>(device, D3D9Slots.CreateDepthStencilSurface);
            stretchRect = ComVtable.Get<StretchRectDelegate>(device, D3D9Slots.StretchRect);
            setRenderTarget = ComVtable.Get<SetRenderTargetDelegate>(device, D3D9Slots.SetRenderTarget);
            setDepthStencilSurface = ComVtable.Get<SetDepthStencilSurfaceDelegate>(device, D3D9Slots.SetDepthStencilSurface);
            beginScene = ComVtable.Get<BeginSceneDelegate>(device, D3D9Slots.BeginScene);
            endScene = ComVtable.Get<EndSceneDelegate>(device, D3D9Slots.EndScene);
            clear = ComVtable.Get<ClearDelegate>(device, D3D9Slots.Clear);
            setTransform = ComVtable.Get<SetTransformDelegate>(device, D3D9Slots.SetTransform);
            setViewport = ComVtable.Get<SetViewportDelegate>(device, D3D9Slots.SetViewport);
            setLight = ComVtable.Get<SetLightDelegate>(device, D3D9Slots.SetLight);
            lightEnable = ComVtable.Get<LightEnableDelegate>(device, D3D9Slots.LightEnable);
            setRenderState = ComVtable.Get<SetRenderStateDelegate>(device, D3D9Slots.SetRenderState);
            setTexture = ComVtable.Get<SetTextureDelegate>(device, D3D9Slots.SetTexture);
            setTextureStageState = ComVtable.Get<SetTextureStageStateDelegate>(device, D3D9Slots.SetTextureStageState);
            setSamplerState = ComVtable.Get<SetSamplerStateDelegate>(device, D3D9Slots.SetSamplerState);
            setScissorRect = ComVtable.Get<SetScissorRectDelegate>(device, D3D9Slots.SetScissorRect);
            drawPrimitive = ComVtable.Get<DrawPrimitiveDelegate>(device, D3D9Slots.DrawPrimitive);
            drawIndexedPrimitive = ComVtable.Get<DrawIndexedPrimitiveDelegate>(device, D3D9Slots.DrawIndexedPrimitive);
            drawPrimitiveUp = ComVtable.Get<DrawPrimitiveUpDelegate>(device, D3D9Slots.DrawPrimitiveUp);
            setFvf = ComVtable.Get<SetFvfDelegate>(device, D3D9Slots.SetFvf);
            createVertexShader = ComVtable.Get<CreateVertexShaderDelegate>(device, D3D9Slots.CreateVertexShader);
            setVertexShader = ComVtable.Get<SetVertexShaderDelegate>(device, D3D9Slots.SetVertexShader);
            setVertexShaderConstantF = ComVtable.Get<SetVertexShaderConstantFDelegate>(device, D3D9Slots.SetVertexShaderConstantF);
            setStreamSource = ComVtable.Get<SetStreamSourceDelegate>(device, D3D9Slots.SetStreamSource);
            setIndices = ComVtable.Get<SetIndicesDelegate>(device, D3D9Slots.SetIndices);
            createPixelShader = ComVtable.Get<CreatePixelShaderDelegate>(device, D3D9Slots.CreatePixelShader);
            setPixelShader = ComVtable.Get<SetPixelShaderDelegate>(device, D3D9Slots.SetPixelShader);
            setPixelShaderConstantF = ComVtable.Get<SetPixelShaderConstantFDelegate>(device, D3D9Slots.SetPixelShaderConstantF);
        }

        private void ReadCaps()
        {
            var getCaps = ComVtable.Get<GetDeviceCapsDelegate>(d3d, D3D9Slots.GetDeviceCaps);
            var result = getCaps(d3d, 0, (uint)D3D9.DeviceType.Hal, ref caps);

            if (result != D3D9.Ok)
            {
                EngineLog.Warn("graphics", "GetDeviceCaps failed: 0x" + result.ToString("X8") +
                                           "; assuming the minimum shader model.");
                caps = new D3DCaps9();
            }

            EngineLog.Info("graphics", "Shader model: vertex " + caps.VertexShaderMajor +
                                       ".x, pixel " + caps.PixelShaderMajor +
                                       ".x; max texture " + caps.MaxTextureWidth + "x" + caps.MaxTextureHeight + ".");
        }

        private D3DPresentParameters BuildPresentParameters(uint behavior)
        {
            var parameters = new D3DPresentParameters();

            parameters.BackBufferWidth = (uint)width;
            parameters.BackBufferHeight = (uint)height;

            // Windowed mode wants the desktop format: a windowed swap chain in a
            // format the desktop is not in fails on some drivers.
            parameters.BackBufferFormat = (uint)(windowed ? D3D9.Format.X8R8G8B8 : D3D9.Format.A8R8G8B8);
            parameters.BackBufferCount = 1;
            parameters.MultiSampleType = (uint)D3D9.MultiSampleType.None;
            parameters.MultiSampleQuality = 0;
            parameters.SwapEffect = (uint)D3D9.SwapEffect.Discard;
            parameters.DeviceWindow = window;
            parameters.Windowed = windowed;
            parameters.EnableAutoDepthStencil = true;
            parameters.AutoDepthStencilFormat = (uint)D3D9.Format.D24S8;

            // Lockable back buffer makes frame capture and the render-target
            // effects work without an extra copy per frame.
            parameters.Flags = D3D9.Present.LockableBackBuffer;
            parameters.FullScreenRefreshRateInHz = 0;
            parameters.PresentationInterval = vsync
                ? D3D9.Present.IntervalOne
                : D3D9.Present.IntervalImmediate;

            return parameters;
        }

        // ---------------------------------------------------------------- frame

        internal void BeginScene()
        {
            if (lost) return;
            if (sceneOpen) throw new InvalidOperationException("A scene is already open.");

            var result = beginScene(device);
            Check(result, "BeginScene");
            sceneOpen = true;
        }

        internal void EndScene()
        {
            if (!sceneOpen) return;

            sceneOpen = false;
            var result = endScene(device);
            Check(result, "EndScene");
        }

        internal void Present()
        {
            if (lost) return;

            var result = present(device, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            // A lost device on Present is the normal alt-tab path, not an error.
            if (result == D3D9.DeviceLost)
            {
                lost = true;
                return;
            }

            Check(result, "Present");
        }

        internal void Clear(uint argb, bool clearTarget, bool clearDepth, bool clearStencil)
        {
            if (lost) return;

            var flags = 0u;
            if (clearTarget) flags |= D3D9.Clear.Target;
            if (clearDepth) flags |= D3D9.Clear.ZBuffer;
            if (clearStencil) flags |= D3D9.Clear.Stencil;

            var result = clear(device, 0, IntPtr.Zero, flags, argb, 1f, 0);
            Check(result, "Clear");
        }

        // ---------------------------------------------------------------- transforms

        internal void SetTransform(uint state, ref Matrix matrix)
        {
            if (lost) return;

            D3DMatrix native;
            D3DMatrix.FromMatrix(matrix, out native);

            var result = setTransform(device, state, ref native.M11);
            Check(result, "SetTransform");
        }

        internal void SetViewport(int x, int y, int viewportWidth, int viewportHeight)
        {
            if (lost) return;

            var viewport = new D3DViewport();
            viewport.X = (uint)x;
            viewport.Y = (uint)y;
            viewport.Width = (uint)viewportWidth;
            viewport.Height = (uint)viewportHeight;
            viewport.MinZ = 0f;
            viewport.MaxZ = 1f;

            var result = setViewport(device, ref viewport);
            Check(result, "SetViewport");
        }

        internal void SetScissorRectangle(int x, int y, int rectWidth, int rectHeight)
        {
            if (lost) return;

            var rect = new D3DRect();
            rect.Left = x;
            rect.Top = y;
            rect.Right = x + rectWidth;
            rect.Bottom = y + rectHeight;

            var result = setScissorRect(device, ref rect);
            Check(result, "SetScissorRect");
        }

        // ---------------------------------------------------------------- render state

        internal void SetRenderState(uint state, uint value)
        {
            if (lost) return;

            if (state < 256 && stateSet[state] && stateCache[state] == value) return;

            var result = setRenderState(device, state, value);
            Check(result, "SetRenderState");

            if (state < 256)
            {
                stateCache[state] = value;
                stateSet[state] = true;
            }
        }

        internal void SetRenderState(uint state, bool value)
        {
            SetRenderState(state, value ? 1u : 0u);
        }

        internal void SetRenderState(uint state, D3D9.Blend value)
        {
            SetRenderState(state, (uint)value);
        }

        internal void SetSamplerState(uint sampler, uint type, uint value)
        {
            if (lost) return;

            var index = sampler * 16 + type;

            if (index < samplerCache.Length && samplerSet[index] && samplerCache[index] == value) return;

            var result = setSamplerState(device, sampler, type, value);
            Check(result, "SetSamplerState");

            if (index < samplerCache.Length)
            {
                samplerCache[index] = value;
                samplerSet[index] = true;
            }
        }

        internal void SetTextureStageState(uint stage, uint type, uint value)
        {
            if (lost) return;

            var index = stage * 40 + type;

            if (index < textureStageCache.Length && textureStageSet[index] && textureStageCache[index] == value) return;

            var result = setTextureStageState(device, stage, type, value);
            Check(result, "SetTextureStageState");

            if (index < textureStageCache.Length)
            {
                textureStageCache[index] = value;
                textureStageSet[index] = true;
            }
        }

        internal void SetTexture(uint stage, D3D9Texture texture)
        {
            if (lost) return;

            var handle = texture == null ? IntPtr.Zero : texture.Handle;

            if (stage == 0)
            {
                if (currentTexture0 == handle) return;
                currentTexture0 = handle;
            }
            else if (stage == 1)
            {
                if (currentTexture1 == handle) return;
                currentTexture1 = handle;
            }

            var result = setTexture(device, stage, handle);
            Check(result, "SetTexture");
        }

        internal void SetVertexShader(D3D9VertexShader shader)
        {
            if (lost) return;

            var handle = shader == null ? IntPtr.Zero : shader.Handle;

            if (currentVertexShader == handle) return;
            currentVertexShader = handle;

            var result = setVertexShader(device, handle);
            Check(result, "SetVertexShader");
        }

        internal void SetPixelShader(D3D9PixelShader shader)
        {
            if (lost) return;

            var handle = shader == null ? IntPtr.Zero : shader.Handle;

            if (currentPixelShader == handle) return;
            currentPixelShader = handle;

            var result = setPixelShader(device, handle);
            Check(result, "SetPixelShader");
        }

        internal void SetFvf(uint fvf)
        {
            if (lost) return;

            if (currentFvf == fvf) return;
            currentFvf = fvf;

            var result = setFvf(device, fvf);
            Check(result, "SetFVF");
        }

        internal void SetStreamSource(uint stream, D3D9VertexBuffer buffer, uint stride)
        {
            if (lost) return;

            var handle = buffer == null ? IntPtr.Zero : buffer.Handle;

            if (stream == 0)
            {
                if (currentStream0 == handle) return;
                currentStream0 = handle;
            }

            var result = setStreamSource(device, stream, handle, 0, stride);
            Check(result, "SetStreamSource");
        }

        internal void SetIndices(D3D9IndexBuffer buffer)
        {
            if (lost) return;

            var handle = buffer == null ? IntPtr.Zero : buffer.Handle;

            if (currentIndices == handle) return;
            currentIndices = handle;

            var result = setIndices(device, handle);
            Check(result, "SetIndices");
        }

        // ---------------------------------------------------------------- constants

        internal void SetVertexShaderConstant(int register, ref Matrix matrix)
        {
            if (lost) return;

            var data = new float[16];
            data[0] = matrix.M11; data[1] = matrix.M12; data[2] = matrix.M13; data[3] = matrix.M14;
            data[4] = matrix.M21; data[5] = matrix.M22; data[6] = matrix.M23; data[7] = matrix.M24;
            data[8] = matrix.M31; data[9] = matrix.M32; data[10] = matrix.M33; data[11] = matrix.M34;
            data[12] = matrix.M41; data[13] = matrix.M42; data[14] = matrix.M43; data[15] = matrix.M44;

            var result = setVertexShaderConstantF(device, (uint)register, ref data[0], 4);
            Check(result, "SetVertexShaderConstantF");
        }

        internal void SetVertexShaderConstant(int register, Vector4 value)
        {
            if (lost) return;

            var data = new float[4];
            data[0] = value.X;
            data[1] = value.Y;
            data[2] = value.Z;
            data[3] = value.W;

            var result = setVertexShaderConstantF(device, (uint)register, ref data[0], 1);
            Check(result, "SetVertexShaderConstantF");
        }

        internal void SetPixelShaderConstant(int register, Vector4 value)
        {
            if (lost) return;

            var data = new float[4];
            data[0] = value.X;
            data[1] = value.Y;
            data[2] = value.Z;
            data[3] = value.W;

            var result = setPixelShaderConstantF(device, (uint)register, ref data[0], 1);
            Check(result, "SetPixelShaderConstantF");
        }

        internal void SetPixelShaderConstant(int register, ref Matrix matrix)
        {
            if (lost) return;

            var data = new float[16];
            data[0] = matrix.M11; data[1] = matrix.M12; data[2] = matrix.M13; data[3] = matrix.M14;
            data[4] = matrix.M21; data[5] = matrix.M22; data[6] = matrix.M23; data[7] = matrix.M24;
            data[8] = matrix.M31; data[9] = matrix.M32; data[10] = matrix.M33; data[11] = matrix.M34;
            data[12] = matrix.M41; data[13] = matrix.M42; data[14] = matrix.M43; data[15] = matrix.M44;

            var result = setPixelShaderConstantF(device, (uint)register, ref data[0], 4);
            Check(result, "SetPixelShaderConstantF");
        }

        // ---------------------------------------------------------------- drawing

        internal void DrawPrimitive(D3D9.PrimitiveType primitiveType, uint startVertex, uint primitiveCount)
        {
            if (lost || primitiveCount == 0) return;

            var result = drawPrimitive(device, (uint)primitiveType, startVertex, primitiveCount);
            Check(result, "DrawPrimitive");
        }

        internal void DrawIndexedPrimitive(D3D9.PrimitiveType primitiveType, int baseVertexIndex,
            uint minVertexIndex, uint vertexCount, uint startIndex, uint primitiveCount)
        {
            if (lost || primitiveCount == 0) return;

            var result = drawIndexedPrimitive(device, (uint)primitiveType, baseVertexIndex, minVertexIndex,
                vertexCount, startIndex, primitiveCount);
            Check(result, "DrawIndexedPrimitive");
        }

        internal void DrawPrimitiveUp(D3D9.PrimitiveType primitiveType, uint primitiveCount,
            IntPtr vertexData, uint stride)
        {
            if (lost || primitiveCount == 0) return;

            var result = drawPrimitiveUp(device, (uint)primitiveType, primitiveCount, vertexData, stride);
            Check(result, "DrawPrimitiveUP");
        }

        /// <summary>
        /// Draws a managed vertex array without a vertex buffer.
        ///
        /// Pins the array for the duration of the call. Used by the particle
        /// system, which rebuilds its vertices every frame and would otherwise
        /// have to keep a dynamic buffer in sync.
        /// </summary>
        internal void DrawPrimitiveUp<T>(D3D9.PrimitiveType primitiveType, uint primitiveCount,
            T[] vertices, uint stride) where T : struct
        {
            if (vertices == null) throw new ArgumentNullException("vertices");
            if (lost || primitiveCount == 0) return;

            var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);

            try
            {
                var result = drawPrimitiveUp(device, (uint)primitiveType, primitiveCount,
                    handle.AddrOfPinnedObject(), stride);
                Check(result, "DrawPrimitiveUP");
            }
            finally
            {
                handle.Free();
            }
        }

        // ---------------------------------------------------------------- resources

        internal D3D9Texture CreateTexture(int textureWidth, int textureHeight, int levels, bool renderTarget)
        {
            if (lost) throw new D3D9Exception("CreateTexture", D3D9.DeviceLost);

            var usage = renderTarget ? D3D9.Usage.RenderTarget : 0u;
            var pool = renderTarget ? D3D9.Pool.Default : D3D9.Pool.Managed;

            IntPtr texture;
            var result = createTexture(device, (uint)textureWidth, (uint)textureHeight, (uint)levels,
                usage, (uint)D3D9.Format.A8R8G8B8, (uint)pool, out texture, IntPtr.Zero);

            Check(result, "CreateTexture");

            if (texture == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateTexture returned a null texture.");
            }

            return new D3D9Texture(texture, textureWidth, textureHeight, levels, renderTarget);
        }

        internal D3D9VertexBuffer CreateVertexBuffer(int lengthInBytes, uint fvf, bool dynamic)
        {
            if (lost) throw new D3D9Exception("CreateVertexBuffer", D3D9.DeviceLost);

            var usage = dynamic ? D3D9.Usage.Dynamic | D3D9.Usage.WriteOnly : 0u;
            var pool = dynamic ? D3D9.Pool.Default : D3D9.Pool.Managed;

            IntPtr buffer;
            var result = createVertexBuffer(device, (uint)lengthInBytes, usage, fvf, (uint)pool,
                out buffer, IntPtr.Zero);

            Check(result, "CreateVertexBuffer");

            if (buffer == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateVertexBuffer returned a null buffer.");
            }

            return new D3D9VertexBuffer(buffer, lengthInBytes);
        }

        internal D3D9IndexBuffer CreateIndexBuffer(int lengthInBytes, bool thirtyTwoBit)
        {
            if (lost) throw new D3D9Exception("CreateIndexBuffer", D3D9.DeviceLost);

            var format = thirtyTwoBit ? D3D9.Format.Index32 : D3D9.Format.Index16;

            IntPtr buffer;
            var result = createIndexBuffer(device, (uint)lengthInBytes, 0, (uint)format,
                (uint)D3D9.Pool.Managed, out buffer, IntPtr.Zero);

            Check(result, "CreateIndexBuffer");

            if (buffer == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateIndexBuffer returned a null buffer.");
            }

            return new D3D9IndexBuffer(buffer, lengthInBytes, thirtyTwoBit);
        }

        /// <summary>
        /// Creates an off-screen buffer that can be rendered into and sampled from.
        ///
        /// Built as a render-target texture rather than a bare surface, because
        /// every consumer - blur, feedback, contrast - draws its own previous
        /// result back to the screen. A surface can be a blit destination but not
        /// a shader input, so a surface would only be half the job.
        /// </summary>
        internal D3D9RenderTarget CreateRenderTarget(int targetWidth, int targetHeight)
        {
            if (lost) throw new D3D9Exception("CreateRenderTarget", D3D9.DeviceLost);
            if (targetWidth <= 0) throw new ArgumentOutOfRangeException("targetWidth");
            if (targetHeight <= 0) throw new ArgumentOutOfRangeException("targetHeight");

            IntPtr texture;
            var result = createTexture(device, (uint)targetWidth, (uint)targetHeight, 1,
                D3D9.Usage.RenderTarget, (uint)D3D9.Format.A8R8G8B8, (uint)D3D9.Pool.Default,
                out texture, IntPtr.Zero);

            Check(result, "CreateRenderTarget(texture)");

            if (texture == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateTexture returned a null render-target texture.");
            }

            IntPtr surface = IntPtr.Zero;

            try
            {
                var getSurface = ComVtable.Get<GetSurfaceLevelDelegate>(texture, D3D9Slots.TextureGetSurfaceLevel);
                var surfaceResult = getSurface(texture, 0, out surface);

                if (surfaceResult != D3D9.Ok || surface == IntPtr.Zero)
                {
                    throw new D3D9Exception("GetSurfaceLevel", surfaceResult);
                }

                return new D3D9RenderTarget(texture, surface, targetWidth, targetHeight);
            }
            catch
            {
                if (surface != IntPtr.Zero)
                {
                    ComVtable.Get<ReleaseDelegate>(surface, D3D9Slots.Release)(surface);
                }

                ComVtable.Get<ReleaseDelegate>(texture, D3D9Slots.Release)(texture);
                throw;
            }
        }

        internal D3D9VertexShader CompileVertexShader(string name, string source, string entryPoint)
        {
            if (lost) throw new D3D9Exception("CreateVertexShader", D3D9.DeviceLost);

            var bytecode = ShaderCompiler.Compile(name, source, entryPoint, ShaderLibrary.VertexModel);
            var handle = GCHandle.Alloc(bytecode, GCHandleType.Pinned);

            try
            {
                IntPtr shader;
                var result = createVertexShader(device, handle.AddrOfPinnedObject(), out shader);
                Check(result, "CreateVertexShader");

                if (shader == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateVertexShader returned null for '" + name + "'.");
                }

                return new D3D9VertexShader(shader, name);
            }
            finally
            {
                handle.Free();
            }
        }

        internal D3D9PixelShader CompilePixelShader(string name, string source, string entryPoint)
        {
            if (lost) throw new D3D9Exception("CreatePixelShader", D3D9.DeviceLost);

            var bytecode = ShaderCompiler.Compile(name, source, entryPoint, ShaderLibrary.PixelModel);
            var handle = GCHandle.Alloc(bytecode, GCHandleType.Pinned);

            try
            {
                IntPtr shader;
                var result = createPixelShader(device, handle.AddrOfPinnedObject(), out shader);
                Check(result, "CreatePixelShader");

                if (shader == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreatePixelShader returned null for '" + name + "'.");
                }

                return new D3D9PixelShader(shader, name);
            }
            finally
            {
                handle.Free();
            }
        }

        // ---------------------------------------------------------------- render targets

        /// <summary>
        /// Binds a render target, or the back buffer when null.
        ///
        /// Any texture bound to a sampler is unbound first. Direct3D 9 does not
        /// allow a surface to be a render target and a shader input at the same
        /// time, and on several drivers that combination is a hard crash rather
        /// than a validation error - which is exactly what happened the first time
        /// this engine drew its own frame buffer back to the screen.
        /// </summary>
        internal void SetRenderTarget(D3D9RenderTarget target)
        {
            if (lost) return;

            SetTexture(0, null);
            SetTexture(1, null);

            var handle = target == null ? BackBuffer() : target.Handle;
            var result = setRenderTarget(device, 0, handle);
            Check(result, "SetRenderTarget");

            // The depth-stencil has to match the render target's size.
            //
            // The auto-depth-stencil created with the device belongs to the swap
            // chain and is the window's size. Binding a larger off-screen target
            // while that depth buffer stays bound means every pixel outside the
            // window's rectangle fails the depth test and is discarded - which is
            // why the project's 1024x768 buffer only ever showed content in a
            // 512x384 corner.
            if (target == null)
            {
                setDepthStencilSurface(device, depthStencil);
            }
            else
            {
                setDepthStencilSurface(device, AcquireTargetDepth(target.Width, target.Height));
            }
        }

        /// <summary>
        /// The depth-stencil surface for an off-screen target, created once per size.
        /// </summary>
        private IntPtr AcquireTargetDepth(int targetWidth, int targetHeight)
        {
            if (targetDepth != IntPtr.Zero &&
                targetDepthWidth == targetWidth && targetDepthHeight == targetHeight)
            {
                return targetDepth;
            }

            ReleaseTargetDepth();

            IntPtr surface;
            var result = createDepthStencil(device, (uint)targetWidth, (uint)targetHeight,
                (uint)D3D9.Format.D24S8, (uint)D3D9.MultiSampleType.None, 0, true,
                out surface, IntPtr.Zero);

            if (result != D3D9.Ok || surface == IntPtr.Zero)
            {
                // Depth is optional: the 2D passes do not need it, and a machine
                // that refuses one still renders the frame.
                EngineLog.Warn("graphics", "No depth-stencil for the off-screen target: 0x" +
                                           result.ToString("X8"));
                return IntPtr.Zero;
            }

            targetDepth = surface;
            targetDepthWidth = targetWidth;
            targetDepthHeight = targetHeight;

            return surface;
        }

        private void ReleaseTargetDepth()
        {
            if (targetDepth == IntPtr.Zero) return;

            ReleaseObject(targetDepth);
            targetDepth = IntPtr.Zero;
            targetDepthWidth = 0;
            targetDepthHeight = 0;
        }

        internal IntPtr BackBuffer()
        {
            AcquireBackBuffer();
            return backBuffer;
        }

        /// <summary>
        /// Copies a rectangle of the back buffer into a render target.
        ///
        /// This is how the engine captures the frame for the blur, feedback and
        /// render-to-texture events. StretchRect is a driver blit, so it is far
        /// cheaper than locking and copying pixels.
        /// </summary>
        internal void CopyBackBufferTo(D3D9RenderTarget target, int x, int y, int copyWidth, int copyHeight)
        {
            if (lost) return;
            if (target == null) throw new ArgumentNullException("target");

            // A render target cannot be a blit destination and a shader input at
            // the same time. The post-process effects bind their own slot as a
            // texture and then copy the frame back into it, so the texture has to
            // be released first - otherwise the driver either fails the call or,
            // on several drivers, stalls.
            SetTexture(0, null);
            SetTexture(1, null);

            var source = new D3DRect();
            source.Left = x;
            source.Top = y;
            source.Right = x + copyWidth;
            source.Bottom = y + copyHeight;

            var destination = new D3DRect();
            destination.Left = 0;
            destination.Top = 0;
            destination.Right = target.Width;
            destination.Bottom = target.Height;

            var result = stretchRect(device, BackBuffer(), ref source, target.Handle, ref destination, 1);
            Check(result, "StretchRect");
        }

        /// <summary>
        /// Copies one render target into another.
        ///
        /// This is what the timeline's capture effects use. Going through the window
        /// back buffer instead would be wrong - the project renders into its own
        /// buffer, so the back buffer holds a previous frame - and it also ties the
        /// effects to a swap chain that may never present.
        /// </summary>
        internal void CopyRenderTarget(D3D9RenderTarget source, int x, int y, int copyWidth, int copyHeight,
            D3D9RenderTarget destination)
        {
            if (lost) return;
            if (source == null) throw new ArgumentNullException("source");
            if (destination == null) throw new ArgumentNullException("destination");

            SetTexture(0, null);
            SetTexture(1, null);

            var sourceRect = new D3DRect();
            sourceRect.Left = x;
            sourceRect.Top = y;
            sourceRect.Right = x + copyWidth;
            sourceRect.Bottom = y + copyHeight;

            var destinationRect = new D3DRect();
            destinationRect.Left = 0;
            destinationRect.Top = 0;
            destinationRect.Right = destination.Width;
            destinationRect.Bottom = destination.Height;

            var result = stretchRect(device, source.Handle, ref sourceRect,
                destination.Handle, ref destinationRect, 1);
            Check(result, "StretchRect");
        }

        /// <summary>Copies a render target back into a rectangle of the back buffer.</summary>
        internal void CopyRenderTargetToBackBuffer(D3D9RenderTarget target, int x, int y,
            int copyWidth, int copyHeight)
        {
            if (lost) return;
            if (target == null) throw new ArgumentNullException("target");

            var source = new D3DRect();
            source.Left = 0;
            source.Top = 0;
            source.Right = target.Width;
            source.Bottom = target.Height;

            var destination = new D3DRect();
            destination.Left = x;
            destination.Top = y;
            destination.Right = x + copyWidth;
            destination.Bottom = y + copyHeight;

            var result = stretchRect(device, target.Handle, ref source, BackBuffer(), ref destination, 1);
            Check(result, "StretchRect");
        }

        // ---------------------------------------------------------------- lighting

        internal void SetDirectionalLight(Vector3 direction, ColorRgba ambient)
        {
            if (lost) return;

            var light = new D3DLight9();
            light.Type = 3; // D3DLIGHT_DIRECTIONAL
            light.Diffuse = new D3DColorValue(1f, 1f, 1f, 1f);
            light.Specular = new D3DColorValue(0f, 0f, 0f, 1f);
            light.Ambient = new D3DColorValue(ambient.R / 255f, ambient.G / 255f, ambient.B / 255f, 1f);
            light.Direction = new D3DVector(direction.X, direction.Y, direction.Z);
            light.Range = float.MaxValue;
            light.Falloff = 1f;
            light.Attenuation0 = 1f;
            light.Attenuation1 = 0f;
            light.Attenuation2 = 0f;

            var result = setLight(device, 0, ref light);
            Check(result, "SetLight");

            var enableResult = lightEnable(device, 0, true);
            Check(enableResult, "LightEnable");
        }

        /// <summary>
        /// Copies the back buffer into a BGRA byte array.
        ///
        /// The naive implementation - LockRect straight onto the back buffer -
        /// works on the first few frames and then stalls forever on WDDM drivers
        /// (Vista and later), because locking a back buffer that the GPU is still
        /// presenting blocks until a vblank that never arrives under load. This is
        /// exactly how the engine's first headless run hung at frame nine.
        ///
        /// The correct path is the documented one: copy the back buffer into a
        /// system-memory surface with GetRenderTargetData, then lock that. A
        /// system-memory surface is not in the present chain, so locking it cannot
        /// deadlock, and the copy is a defined synchronisation point.
        ///
        /// Used by the screenshot command, the headless verification and the tests.
        /// The render path itself never reads back, because any readback stalls the
        /// pipeline.
        /// </summary>
        internal byte[] ReadBackBuffer(int readWidth, int readHeight)
        {
            return ReadSurfaceBounded(BackBuffer(), readWidth, readHeight);
        }

        /// <summary>
        /// Blocks until the GPU has finished everything queued so far.
        ///
        /// Implemented with a D3DQUERYTYPE_EVENT query: issue it at the end of the
        /// queue, then poll with D3DGETDATA_FLUSH until it reports completion. The
        /// flush flag is what stops the poll from spinning forever - without it the
        /// driver never starts the work the query is waiting for, which is a deadlock
        /// that looks exactly like a hang.
        ///
        /// Bounded by a timeout: a driver that never completes the query must not
        /// freeze the engine, so after two seconds this gives up and lets the caller
        /// proceed (and probably fail with a real error code).
        /// </summary>
        internal void Flush()
        {
            if (lost || device == IntPtr.Zero) return;

            IntPtr query = IntPtr.Zero;

            try
            {
                var createQuery = ComVtable.Get<CreateQueryDelegate>(device, D3D9Slots.CreateQuery);

                // D3DQUERYTYPE_EVENT is 8.
                var result = createQuery(device, 8, out query);

                if (result != D3D9.Ok || query == IntPtr.Zero) return;

                var issue = ComVtable.Get<QueryIssueDelegate>(query, D3D9Slots.QueryIssue);
                var getData = ComVtable.Get<QueryGetDataDelegate>(query, D3D9Slots.QueryGetData);

                // D3DISSUE_END = 1.
                if (issue(query, 1) != D3D9.Ok) return;

                var data = Marshal.AllocHGlobal(4);
                var deadline = Environment.TickCount + 2000;

                try
                {
                    while (Environment.TickCount < deadline)
                    {
                        // D3DGETDATA_FLUSH = 1.
                        var poll = getData(query, data, 4, 1);

                        if (poll == D3D9.Ok) return;

                        if (poll == D3D9.DeviceLost)
                        {
                            lost = true;
                            return;
                        }

                        System.Threading.Thread.Sleep(0);
                    }

                    EngineLog.Warn("graphics", "GPU flush timed out after 2s.");
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }
            catch (Exception error)
            {
                EngineLog.Verbose("graphics", "Flush unavailable: " + error.Message);
            }
            finally
            {
                if (query != IntPtr.Zero)
                {
                    ReleaseObject(query);
                }
            }
        }

        /// <summary>
        /// Reads an off-screen render target back into memory.
        ///
        /// Runs on a worker thread with a deadline, because a wedged driver must not
        /// be able to freeze the engine. See <see cref="ReadSurfaceBounded"/>.
        /// </summary>
        internal byte[] ReadRenderTarget(D3D9RenderTarget target)
        {
            if (target == null) throw new ArgumentNullException("target");

            return ReadSurfaceBounded(target.Handle, target.Width, target.Height);
        }

        /// <summary>
        /// True once readback has proven unreliable on this device.
        ///
        /// Several WDDM drivers deadlock inside LockRect on the second or third
        /// GetRenderTargetData of a process - the engine hit exactly this, with the
        /// first screenshot of a run working and the second hanging the process with
        /// no error and no crash. There is no way to time out a kernel-mode wait, so
        /// after the first timeout readback is disabled for the rest of the session
        /// and callers get an empty array. Screenshots and headless verification
        /// degrade; the game keeps running.
        /// </summary>
        internal bool ReadbackUnavailable
        {
            get { return readbackDisabled; }
        }

        private byte[] ReadSurfaceBounded(IntPtr source, int readWidth, int readHeight)
        {
            if (readbackDisabled)
            {
                EngineLog.Verbose("graphics", "Readback is disabled for this session; skipping.");
                return new byte[0];
            }

            byte[] pixels = null;
            Exception failure = null;

            // The work happens on a thread that can be abandoned. A thread stuck in
            // the driver is a leaked thread, which is a far better outcome than a
            // frozen process.
            var worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    pixels = ReadSurface(source, readWidth, readHeight);
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });

            worker.IsBackground = true;
            worker.Name = "DPT.Engine.Readback";
            worker.Start();

            if (!worker.Join(ReadbackTimeoutMs))
            {
                readbackDisabled = true;
                EngineLog.Warn("graphics", "Readback did not complete in " + ReadbackTimeoutMs +
                                           " ms; disabling readback for this session. The engine " +
                                           "continues without screenshots.");
                return new byte[0];
            }

            if (failure != null)
            {
                EngineLog.Warn("graphics", "Readback failed: " + failure.Message);
                return new byte[0];
            }

            return pixels ?? new byte[0];
        }

        private const int ReadbackTimeoutMs = 4000;

        private byte[] ReadSurface(IntPtr source, int readWidth, int readHeight)
        {
            if (lost) throw new D3D9Exception("ReadSurface", D3D9.DeviceLost);
            if (readWidth <= 0) throw new ArgumentOutOfRangeException("readWidth");
            if (readHeight <= 0) throw new ArgumentOutOfRangeException("readHeight");

            // Wait for the GPU to finish everything queued so far.
            //
            // GetRenderTargetData is documented to synchronise, and on several WDDM
            // drivers it does not when the device has a deep queue: the call blocks
            // waiting for work that the driver has not been told to start. The
            // engine hit exactly this - the first readback of a run worked, the
            // second hung forever with the process idle.
            //
            // An event query issued at the end of the queue and polled until it
            // completes is the canonical Direct3D 9 way to say "wait for the GPU",
            // and it does not involve the swap chain.
            Flush();

            // GetRenderTargetData requires the source surface to be unbound from the
            // device, and a surface that is also a shader input is not. Unbinding
            // here means callers cannot get this wrong.
            SetTexture(0, null);
            SetTexture(1, null);

            EngineLog.Verbose("graphics", "read: acquire surface" );
            var surface = AcquireReadbackSurface(source, readWidth, readHeight);
            EngineLog.Verbose("graphics", "read: got surface 0x" + surface.ToString("X"));

            var getData = ComVtable.Get<GetRenderTargetDataDelegate>(device, D3D9Slots.GetRenderTargetData);
            EngineLog.Verbose("graphics", "read: calling GetRenderTargetData");
            var result = getData(device, source, surface);
            EngineLog.Verbose("graphics", "read: GetRenderTargetData 0x" + result.ToString("X8"));

            if (result != D3D9.Ok)
            {
                throw new D3D9Exception("GetRenderTargetData", result);
            }

            var lockRect = ComVtable.Get<LockRectDelegate>(surface, D3D9Slots.SurfaceLockRect);
            var unlockRect = ComVtable.Get<UnlockRectDelegate>(surface, D3D9Slots.SurfaceUnlockRect);

            var locked = new D3DLockedRect();

            // D3DLOCK_DONOTWAIT, retried.
            //
            // A blocking LockRect on the copy destination is the other place the
            // engine hung: the copy is asynchronous on several drivers, and a plain
            // lock waits on it in kernel mode where nothing can time it out. With
            // DONOTWAIT the driver reports D3DERR_WASSTILLDRAWING instead, and this
            // loop retries with a deadline - so a driver that never finishes costs a
            // logged warning rather than a frozen process.
            var lockResult = D3D9.WasStillDrawing;
            var deadline = Environment.TickCount + 3000;

            while (Environment.TickCount < deadline)
            {
                lockResult = lockRect(surface, ref locked, IntPtr.Zero,
                    D3D9.Lock.ReadOnly | D3D9.Lock.DoNotWait);

                if (lockResult == D3D9.Ok) break;
                if (lockResult != D3D9.WasStillDrawing) break;

                System.Threading.Thread.Sleep(1);
            }

            if (lockResult == D3D9.WasStillDrawing)
            {
                EngineLog.Warn("graphics", "Readback surface still busy after 3s; skipping this read.");
                return new byte[0];
            }

            if (lockResult != D3D9.Ok)
            {
                throw new D3D9Exception("ReadbackSurface.LockRect", lockResult);
            }

            try
            {
                if (locked.Bits == IntPtr.Zero || locked.Pitch < readWidth * 4)
                {
                    throw new InvalidOperationException("Readback lock returned an unusable pointer " +
                                                        "(pitch " + locked.Pitch + ").");
                }

                var pixels = new byte[readWidth * readHeight * 4];
                var rowBytes = readWidth * 4;

                for (var y = 0; y < readHeight; y++)
                {
                    var row = new IntPtr(locked.Bits.ToInt64() + (long)y * locked.Pitch);
                    Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
                }

                return pixels;
            }
            finally
            {
                unlockRect(surface);
            }
        }

        /// <summary>
        /// The system-memory surface used for readback, created once per size.
        ///
        /// Reused because creating a surface per screenshot would leak video memory
        /// on a machine that takes a screenshot every frame from a script.
        /// </summary>
        private IntPtr AcquireReadbackSurface(IntPtr source, int readWidth, int readHeight)
        {
            // The system-memory surface must use exactly the source's format:
            // GetRenderTargetData is a copy, not a conversion, and mismatched
            // formats fail with D3DERR_INVALIDCALL. In windowed mode the back
            // buffer is X8R8G8B8 (the desktop format) while a render target is
            // A8R8G8B8, so the format is read from the source rather than assumed.
            var format = D3D9.Format.A8R8G8B8;

            if (source != IntPtr.Zero)
            {
                var describe = ComVtable.Get<GetSurfaceDescDelegate>(source, D3D9Slots.SurfaceGetDesc);
                var description = new D3DSurfaceDescription();

                if (describe(source, ref description) == D3D9.Ok && description.Format != 0)
                {
                    format = (D3D9.Format)description.Format;
                }
            }

            if (readbackSurface != IntPtr.Zero &&
                readbackWidth == readWidth && readbackHeight == readHeight &&
                readbackFormat == format &&
                Environment.GetEnvironmentVariable("DPT_FRESH_READBACK") == null)
            {
                return readbackSurface;
            }

            ReleaseReadbackSurface();

            var create = ComVtable.Get<CreateOffscreenPlainSurfaceDelegate>(
                device, D3D9Slots.CreateOffscreenPlainSurface);

            IntPtr surface;
            var result = create(device, (uint)readWidth, (uint)readHeight,
                (uint)format, (uint)D3D9.Pool.SystemMem, out surface, IntPtr.Zero);

            Check(result, "CreateOffscreenPlainSurface");

            if (surface == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateOffscreenPlainSurface returned null.");
            }

            readbackSurface = surface;
            readbackWidth = readWidth;
            readbackHeight = readHeight;
            readbackFormat = format;

            return surface;
        }

        private void ReleaseReadbackSurface()
        {
            if (readbackSurface == IntPtr.Zero) return;

            ReleaseObject(readbackSurface);
            readbackSurface = IntPtr.Zero;
            readbackWidth = 0;
            readbackHeight = 0;
        }

        // ---------------------------------------------------------------- device loss

        /// <summary>
        /// Returns true when rendering may continue. False means the device is lost
        /// and the caller should stop drawing this frame.
        /// </summary>
        internal bool CheckDevice()
        {
            if (device == IntPtr.Zero) return false;

            var result = testCooperativeLevel(device);

            if (result == D3D9.Ok)
            {
                lost = false;
                return true;
            }

            lost = true;
            return false;
        }

        /// <summary>
        /// Recreates the swap chain after a loss.
        ///
        /// Callers must release default-pool resources first (render targets,
        /// dynamic buffers); managed resources are restored by the driver.
        /// </summary>
        internal void Reset(int newWidth, int newHeight, bool fullScreen, bool enableVsync)
        {
            if (device == IntPtr.Zero) throw new InvalidOperationException("The device has not been created.");

            width = newWidth;
            height = newHeight;
            windowed = !fullScreen;
            vsync = enableVsync;

            ReleaseBackBuffer();
            ReleaseDepthStencil();
            ReleaseReadbackSurface();

            var parameters = BuildPresentParameters(D3D9.Behavior.HardwareVertexProcessing);
            var result = reset(device, ref parameters);

            if (result != D3D9.Ok)
            {
                EngineLog.Error("graphics", "Reset failed: 0x" + result.ToString("X8"));
                lost = true;
                return;
            }

            lost = false;
            InvalidateState();
            AcquireBackBuffer();
            AcquireDepthStencil();

            EngineLog.Info("graphics", "Device reset to " + width + "x" + height + ".");
        }

        private void AcquireBackBuffer()
        {
            if (backBuffer != IntPtr.Zero) return;

            IntPtr surface;
            var result = getBackBuffer(device, 0, 0, D3D9.BackBuffer.TypeMono, out surface);
            Check(result, "GetBackBuffer");
            backBuffer = surface;
        }

        private void AcquireDepthStencil()
        {
            if (depthStencil != IntPtr.Zero) return;

            IntPtr surface;
            var result = createDepthStencil(device, (uint)width, (uint)height,
                (uint)D3D9.Format.D24S8, (uint)D3D9.MultiSampleType.None, 0, true,
                out surface, IntPtr.Zero);

            if (result != D3D9.Ok || surface == IntPtr.Zero)
            {
                // Depth is optional for the 2D passes; a machine that cannot give
                // us one still renders, just without depth sorting.
                EngineLog.Warn("graphics", "Depth-stencil unavailable: 0x" + result.ToString("X8"));
                return;
            }

            depthStencil = surface;
            setDepthStencilSurface(device, depthStencil);
        }

        private void ReleaseBackBuffer()
        {
            if (backBuffer == IntPtr.Zero) return;

            ReleaseObject(backBuffer);
            backBuffer = IntPtr.Zero;
        }

        private void ReleaseDepthStencil()
        {
            if (depthStencil == IntPtr.Zero) return;

            setDepthStencilSurface(device, IntPtr.Zero);
            ReleaseObject(depthStencil);
            depthStencil = IntPtr.Zero;
        }

        /// <summary>
        /// Releases any COM object by resolving Release from that object's own
        /// vtable.
        ///
        /// This has to be per-object. A function pointer taken from the device's
        /// vtable is the device's Release implementation; calling it with a
        /// surface pointer as 'this' is undefined behaviour and crashes on some
        /// drivers. Every object's Release lives at the same slot index but is a
        /// different function.
        /// </summary>
        private static void ReleaseObject(IntPtr instance)
        {
            if (instance == IntPtr.Zero) return;

            var release = ComVtable.Get<ReleaseDelegate>(instance, D3D9Slots.Release);
            release(instance);
        }

        private void InvalidateState()
        {
            for (var i = 0; i < stateSet.Length; i++) stateSet[i] = false;
            for (var i = 0; i < samplerSet.Length; i++) samplerSet[i] = false;
            for (var i = 0; i < textureStageSet.Length; i++) textureStageSet[i] = false;

            currentTexture0 = IntPtr.Zero;
            currentTexture1 = IntPtr.Zero;
            currentVertexShader = IntPtr.Zero;
            currentPixelShader = IntPtr.Zero;
            currentFvf = uint.MaxValue;
            currentStream0 = IntPtr.Zero;
            currentIndices = IntPtr.Zero;
        }

        // ---------------------------------------------------------------- helpers

        private void Check(int result, string operation)
        {
            if (result == D3D9.Ok) return;

            if (result == D3D9.DeviceLost || result == D3D9.DeviceNotReset)
            {
                lost = true;
                return;
            }

            throw new D3D9Exception(operation, result);
        }

        private void ReleaseD3D()
        {
            if (d3d == IntPtr.Zero) return;

            ReleaseObject(d3d);
            d3d = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (device != IntPtr.Zero)
            {
                ReleaseReadbackSurface();
                ReleaseBackBuffer();
                ReleaseDepthStencil();
                ReleaseObject(device);
                device = IntPtr.Zero;
            }

            ReleaseD3D();
        }
    }

    /// <summary>A Direct3D call returned a failure code.</summary>
    internal sealed class D3D9Exception : Exception
    {
        internal readonly int Result;
        internal readonly string Operation;

        internal D3D9Exception(string operation, int result)
            : base(operation + " failed: 0x" + result.ToString("X8") + " (" + Describe(result) + ")")
        {
            Operation = operation;
            Result = result;
        }

        private static string Describe(int result)
        {
            switch (result)
            {
                case D3D9.DeviceLost: return "device lost";
                case D3D9.DeviceNotReset: return "device not reset";
                case D3D9.NotAvailable: return "not available";
                case D3D9.DriverInvalidCall: return "driver invalid call";
                default: return "unknown";
            }
        }
    }
}
