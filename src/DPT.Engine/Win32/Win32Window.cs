//
// DPT.Engine - the engine window.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

using DPT.Engine.Shared.Diagnostics;

namespace DPT.Engine.Win32
{
    /// <summary>
    /// The engine's only window.
    ///
    /// A plain user32 window with a custom window procedure. No WinForms: the
    /// runtime has to start on Windows XP, and the launcher - which does use
    /// WinForms for its browser control - is a separate executable so the
    /// runtime never loads that stack.
    ///
    /// Two details that are easy to get wrong and expensive to debug:
    ///
    /// * The window class name is unique per instance. Two engine windows in one
    ///   process (the runtime plus an editor preview) would otherwise collide on
    ///   RegisterClassEx, and the second registration fails with a class-already-
    ///   exists error that looks like a resource leak.
    ///
    /// * The WndProc delegate is rooted in a field for the window's whole
    ///   lifetime. The GC would otherwise collect the delegate while native code
    ///   still holds the function pointer, and the next message jumps into freed
    ///   memory.
    /// </summary>
    public sealed class Win32Window : IDisposable
    {
        private readonly string className;
        private readonly InputState input = new InputState();

        private NativeMethods.WndProc windowProcedure;
        private IntPtr instance;
        private IntPtr handle;
        private bool classRegistered;
        private bool disposed;

        private int clientWidth;
        private int clientHeight;

        private bool fullScreen;
        private NativeMethods.Rect windowedPlacement;
        private uint windowedStyle;

        private bool mouseCaptured;
        private bool cursorHidden;
        private bool resizing;

        /// <summary>Raised when the user asks to close the window.</summary>
        public event Action CloseRequested;

        /// <summary>Raised after a resize, with the new client size.</summary>
        public event Action<int, int> Resized;

        public Win32Window()
        {
            // A per-instance class name keeps two windows in one process from
            // fighting over a single registered class.
            className = "DPTEngine.Window." + Guid.NewGuid().ToString("N");
        }

        public IntPtr Handle
        {
            get { return handle; }
        }

        public int ClientWidth
        {
            get { return clientWidth; }
        }

        public int ClientHeight
        {
            get { return clientHeight; }
        }

        public bool IsFullScreen
        {
            get { return fullScreen; }
        }

        public InputState Input
        {
            get { return input; }
        }

        /// <summary>
        /// True while the window is being resized or moved by the user. The
        /// renderer skips frames in this state: drawing into a window that the
        /// shell is dragging is what makes a game stutter under the mouse.
        /// </summary>
        public bool IsResizing
        {
            get { return resizing; }
        }

        // ---------------------------------------------------------------- creation

        public void Create(string title, int width, int height, bool startFullScreen)
        {
            if (disposed) throw new ObjectDisposedException("Win32Window");
            if (handle != IntPtr.Zero) throw new InvalidOperationException("The window is already created.");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            instance = NativeMethods.GetModuleHandle(null);

            if (instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetModuleHandleW failed: " + Marshal.GetLastWin32Error());
            }

            windowProcedure = WindowProcedure;

            var windowClass = new NativeMethods.WndClassEx();
            windowClass.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.WndClassEx));
            windowClass.Style = NativeMethods.CsHRedraw | NativeMethods.CsVRedraw | NativeMethods.CsOwnDc;
            windowClass.WndProc = Marshal.GetFunctionPointerForDelegate(windowProcedure);
            windowClass.Instance = instance;
            windowClass.Cursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(NativeMethods.IdcArrow));
            windowClass.ClassName = className;

            try
            {
                if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
                {
                    throw new InvalidOperationException("RegisterClassExW failed: " + Marshal.GetLastWin32Error());
                }

                classRegistered = true;

                var bounds = new NativeMethods.Rect();
                bounds.Right = width;
                bounds.Bottom = height;

                if (!NativeMethods.AdjustWindowRect(ref bounds, NativeMethods.WsOverlappedWindow, false))
                {
                    throw new InvalidOperationException("AdjustWindowRect failed: " + Marshal.GetLastWin32Error());
                }

                handle = NativeMethods.CreateWindowEx(0, className, title, NativeMethods.WsOverlappedWindow,
                    64, 64, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateWindowExW failed: " + Marshal.GetLastWin32Error());
                }

                UpdateClientSize();

                NativeMethods.ShowWindow(handle, NativeMethods.SwShow);
                NativeMethods.UpdateWindow(handle);
                NativeMethods.SetForegroundWindow(handle);

                if (startFullScreen)
                {
                    SetFullScreen(true, width, height);
                }

                EngineLog.Info("window", "Window created: " + width + "x" + height + ".");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a window that is never shown. Used by the headless self-test and
        /// by the editor's background preview, both of which need a real window
        /// handle for Direct3D but must not pop up in front of the user.
        /// </summary>
        public void CreateHidden(string title, int width, int height)
        {
            Create(title, width, height, false);
            NativeMethods.ShowWindow(handle, NativeMethods.SwHide);
        }

        // ---------------------------------------------------------------- messages

        /// <summary>
        /// Drains the message queue.
        ///
        /// Returns false when the window has been asked to close. Messages for
        /// other windows in the same thread are dispatched normally, because the
        /// editor's web view runs on its own thread and posts to itself.
        /// </summary>
        public bool PumpMessages()
        {
            if (disposed) return false;

            var message = new NativeMethods.Message();

            while (NativeMethods.PeekMessage(ref message, IntPtr.Zero, 0, 0, NativeMethods.PmRemove))
            {
                if (message.MessageId == NativeMethods.WmQuit)
                {
                    return false;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            return handle != IntPtr.Zero;
        }

        private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case NativeMethods.WmClose:
                    RaiseCloseRequested();
                    return IntPtr.Zero;

                case NativeMethods.WmDestroy:
                    handle = IntPtr.Zero;
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;

                case NativeMethods.WmSize:
                    UpdateClientSize();
                    resizing = (wParam.ToInt64() & 0xFFFF) == 0; // SIZE_RESTORED
                    return IntPtr.Zero;

                case NativeMethods.WmEnterSizeMove:
                    resizing = true;
                    return IntPtr.Zero;

                case NativeMethods.WmExitSizeMove:
                    resizing = false;
                    return IntPtr.Zero;

                case NativeMethods.WmSetFocus:
                    input.HasFocus = true;
                    return IntPtr.Zero;

                case NativeMethods.WmKillFocus:
                    input.HasFocus = false;
                    input.Reset();
                    ReleaseMouseCapture();
                    return IntPtr.Zero;

                case NativeMethods.WmKeyDown:
                case NativeMethods.WmSysKeyDown:
                    // Bit 30 of lParam marks an auto-repeat; OnKeyDown filters it.
                    input.OnKeyDown(wParam.ToInt32());
                    return IntPtr.Zero;

                case NativeMethods.WmKeyUp:
                case NativeMethods.WmSysKeyUp:
                    input.OnKeyUp(wParam.ToInt32());
                    return IntPtr.Zero;

                case NativeMethods.WmChar:
                    input.OnChar((char)(wParam.ToInt32() & 0xFFFF));
                    return IntPtr.Zero;

                case NativeMethods.WmMouseMove:
                    input.OnMouseMove(LowWord(lParam), HighWord(lParam));
                    return IntPtr.Zero;

                case NativeMethods.WmLButtonDown:
                    NativeMethods.SetCapture(hWnd);
                    input.OnButtonDown(MouseButtons.Left);
                    return IntPtr.Zero;

                case NativeMethods.WmLButtonUp:
                    NativeMethods.ReleaseCapture();
                    input.OnButtonUp(MouseButtons.Left);
                    return IntPtr.Zero;

                case NativeMethods.WmRButtonDown:
                    input.OnButtonDown(MouseButtons.Right);
                    return IntPtr.Zero;

                case NativeMethods.WmRButtonUp:
                    input.OnButtonUp(MouseButtons.Right);
                    return IntPtr.Zero;

                case NativeMethods.WmMButtonDown:
                    input.OnButtonDown(MouseButtons.Middle);
                    return IntPtr.Zero;

                case NativeMethods.WmMButtonUp:
                    input.OnButtonUp(MouseButtons.Middle);
                    return IntPtr.Zero;

                case NativeMethods.WmMouseWheel:
                    // WHEEL_DELTA is 120 per notch; the sign comes from the high word.
                    input.OnWheel(HighWord(wParam) / 120);
                    return IntPtr.Zero;

                case NativeMethods.WmCaptureChanged:
                    if (!mouseCaptured)
                    {
                        input.Reset();
                    }

                    return IntPtr.Zero;

                case NativeMethods.WmEraseBackground:
                    // Claim the erase so the window never flashes grey behind the
                    // frame that Direct3D is about to present.
                    return new IntPtr(1);
            }

            return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }

        private void RaiseCloseRequested()
        {
            var handler = CloseRequested;

            if (handler != null)
            {
                FaultGuard.Guard("window", "CloseRequested", delegate { handler(); });
            }
            else
            {
                NativeMethods.DestroyWindow(handle);
            }
        }

        private void UpdateClientSize()
        {
            if (handle == IntPtr.Zero) return;

            var rectangle = new NativeMethods.Rect();

            if (!NativeMethods.GetClientRect(handle, ref rectangle)) return;

            var width = rectangle.Width;
            var height = rectangle.Height;

            if (width == clientWidth && height == clientHeight) return;

            clientWidth = width;
            clientHeight = height;

            var handler = Resized;

            if (handler != null)
            {
                FaultGuard.Guard("window", "Resized", delegate { handler(width, height); });
            }
        }

        // ---------------------------------------------------------------- size

        /// <summary>Resizes the client area, keeping the window on screen.</summary>
        public void ResizeClient(int width, int height)
        {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The window has not been created.");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            var bounds = new NativeMethods.Rect();
            bounds.Right = width;
            bounds.Bottom = height;

            if (!NativeMethods.AdjustWindowRect(ref bounds, NativeMethods.WsOverlappedWindow, false))
            {
                throw new InvalidOperationException("AdjustWindowRect failed: " + Marshal.GetLastWin32Error());
            }

            if (!NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
            {
                throw new InvalidOperationException("SetWindowPos failed: " + Marshal.GetLastWin32Error());
            }

            UpdateClientSize();
        }

        /// <summary>
        /// Switches between a decorated window and a borderless full-screen one.
        ///
        /// The windowed placement is remembered so that leaving full screen puts
        /// the window back where it was, rather than in the corner.
        /// </summary>
        public void SetFullScreen(bool enable, int width, int height)
        {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The window has not been created.");
            if (enable == fullScreen) return;

            if (enable)
            {
                var bounds = new NativeMethods.Rect();
                NativeMethods.GetWindowRect(handle, ref bounds);
                windowedPlacement = bounds;
                windowedStyle = (uint)NativeMethods.GetWindowLong(handle, NativeMethods.GwlStyle);

                var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
                var info = new NativeMethods.MonitorInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.MonitorInfo));

                var left = 0;
                var top = 0;
                var monitorWidth = width;
                var monitorHeight = height;

                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    left = info.Monitor.Left;
                    top = info.Monitor.Top;
                    monitorWidth = info.Monitor.Width;
                    monitorHeight = info.Monitor.Height;
                }

                NativeMethods.SetWindowLong(handle, NativeMethods.GwlStyle, unchecked((int)NativeMethods.WsPopup));

                NativeMethods.SetWindowPos(handle, IntPtr.Zero, left, top, monitorWidth, monitorHeight,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);

                fullScreen = true;
            }
            else
            {
                NativeMethods.SetWindowLong(handle, NativeMethods.GwlStyle, (int)windowedStyle);

                NativeMethods.SetWindowPos(handle, IntPtr.Zero,
                    windowedPlacement.Left, windowedPlacement.Top,
                    windowedPlacement.Width, windowedPlacement.Height,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);

                fullScreen = false;
            }

            UpdateClientSize();
        }

        // ---------------------------------------------------------------- cursor

        /// <summary>
        /// Hides the cursor and locks it to the window, for mouse-look.
        ///
        /// ClipCursor rather than a re-centring loop: the OS keeps the pointer
        /// inside the rectangle, so the game sees the true delta without fighting
        /// the cursor every frame.
        /// </summary>
        public void CaptureMouse(bool capture)
        {
            if (handle == IntPtr.Zero) return;

            if (capture == mouseCaptured) return;

            mouseCaptured = capture;

            if (capture)
            {
                var rectangle = new NativeMethods.Rect();
                NativeMethods.GetClientRect(handle, ref rectangle);

                var topLeft = new NativeMethods.Point();
                topLeft.X = rectangle.Left;
                topLeft.Y = rectangle.Top;
                NativeMethods.ClientToScreen(handle, ref topLeft);

                var screen = new NativeMethods.Rect();
                screen.Left = topLeft.X;
                screen.Top = topLeft.Y;
                screen.Right = topLeft.X + rectangle.Width;
                screen.Bottom = topLeft.Y + rectangle.Height;

                NativeMethods.ClipCursor(ref screen);
                NativeMethods.SetCapture(handle);
            }
            else
            {
                ReleaseMouseCapture();
            }
        }

        private void ReleaseMouseCapture()
        {
            if (!mouseCaptured) return;

            mouseCaptured = false;
            NativeMethods.ClipCursorEmpty(IntPtr.Zero);
            NativeMethods.ReleaseCapture();
        }

        /// <summary>Shows or hides the arrow. Reference-counted by the OS, so call sparingly.</summary>
        public void ShowCursor(bool show)
        {
            if (show == !cursorHidden) return;

            cursorHidden = !show;
            NativeMethods.ShowCursor(show);
        }

        // ---------------------------------------------------------------- helpers

        private static int LowWord(IntPtr value)
        {
            return unchecked((short)(value.ToInt64() & 0xFFFF));
        }

        private static int HighWord(IntPtr value)
        {
            return unchecked((short)((value.ToInt64() >> 16) & 0xFFFF));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ReleaseMouseCapture();

            if (handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(handle);
                handle = IntPtr.Zero;
            }

            if (classRegistered)
            {
                NativeMethods.UnregisterClass(className, instance);
                classRegistered = false;
            }

            // Native code can no longer dispatch once the class is unregistered;
            // until both are done this field is what keeps the delegate alive.
            windowProcedure = null;
        }
    }
}
