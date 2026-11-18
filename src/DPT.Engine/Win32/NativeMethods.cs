//
// DPT.Engine - Win32 declarations.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

namespace DPT.Engine.Win32
{
    /// <summary>
    /// Every native entry point the engine uses, in one place.
    ///
    /// The list is deliberately short: user32, kernel32, gdi32 and winmm. No
    /// shell32 (it drags in COM initialisation), no dwmapi (it does not exist on
    /// XP), no dxgi. Optional post-XP entry points are resolved through
    /// <see cref="TryLoad"/> so the engine runs on SP3 as well as on Windows 11.
    ///
    /// All string entry points are explicit 'W' variants with ExactSpelling, so
    /// the marshaller never has to guess between ANSI and Unicode.
    /// </summary>
    internal static class NativeMethods
    {
        // ---------------------------------------------------------------- constants

        internal const uint WsOverlappedWindow = 0x00CF0000;
        internal const uint WsPopup = 0x80000000;
        internal const uint WsVisible = 0x10000000;

        internal const uint CsHRedraw = 0x0002;
        internal const uint CsVRedraw = 0x0001;
        internal const uint CsOwnDc = 0x0020;

        internal const uint PmRemove = 0x0001;
        internal const uint PmNoYield = 0x0002;

        internal const uint WmDestroy = 0x0002;
        internal const uint WmSize = 0x0005;
        internal const uint WmActivate = 0x0006;
        internal const uint WmSetFocus = 0x0007;
        internal const uint WmKillFocus = 0x0008;
        internal const uint WmClose = 0x0010;
        internal const uint WmQuit = 0x0012;
        internal const uint WmEraseBackground = 0x0014;
        internal const uint WmSysCommand = 0x0112;
        internal const uint WmSysKeyDown = 0x0104;
        internal const uint WmSysKeyUp = 0x0105;
        internal const uint WmKeyDown = 0x0100;
        internal const uint WmKeyUp = 0x0101;
        internal const uint WmChar = 0x0102;
        internal const uint WmMouseMove = 0x0200;
        internal const uint WmLButtonDown = 0x0201;
        internal const uint WmLButtonUp = 0x0202;
        internal const uint WmLButtonDblClk = 0x0203;
        internal const uint WmRButtonDown = 0x0204;
        internal const uint WmRButtonUp = 0x0205;
        internal const uint WmRButtonDblClk = 0x0206;
        internal const uint WmMButtonDown = 0x0207;
        internal const uint WmMButtonUp = 0x0208;
        internal const uint WmMouseWheel = 0x020A;
        internal const uint WmCaptureChanged = 0x0215;
        internal const uint WmEnterSizeMove = 0x0231;
        internal const uint WmExitSizeMove = 0x0232;
        internal const uint WmDpiChanged = 0x02E0;

        internal const int SwShow = 5;
        internal const int SwHide = 0;

        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpFrameChanged = 0x0020;

        internal const int GwlStyle = -16;
        internal const int GwlExStyle = -20;
        internal const uint WsExAppWindow = 0x00040000;

        internal const int IdcArrow = 32512;

        internal const int ScKeyMenu = 0xF100;
        internal const int ScRestore = 0xF120;
        internal const int ScMinimize = 0xF020;
        internal const int ScMaximize = 0xF030;

        internal const int SmCxScreen = 0;
        internal const int SmCyScreen = 1;
        internal const int SmCxBorder = 5;
        internal const int SmCyBorder = 6;
        internal const int SmCxFixedFrame = 7;
        internal const int SmCyFixedFrame = 8;

        internal const uint MonitorDefaultToNearest = 2;

        internal const int DispChangeSuccessful = 0;
        internal const int DispChangeRestart = 1;

        internal const uint CdsFullscreen = 0x00000004;
        internal const int DmPelsWidth = 0x00080000;
        internal const int DmPelsHeight = 0x00100000;
        internal const int DmBitsPerPel = 0x00040000;
        internal const int DmDisplayFrequency = 0x00400000;

        internal const uint TimerPeriodMs = 1;

        // ---------------------------------------------------------------- structures

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            internal uint Size;
            internal uint Style;
            internal IntPtr WndProc;
            internal int ClassExtra;
            internal int WindowExtra;
            internal IntPtr Instance;
            internal IntPtr Icon;
            internal IntPtr Cursor;
            internal IntPtr BackgroundBrush;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string ClassName;
            internal IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal IntPtr Window;
            internal uint MessageId;
            internal IntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal int Width { get { return Right - Left; } }
            internal int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal uint Size;
            internal Rect Monitor;
            internal Rect Work;
            internal uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DevMode
        {
            private const int CchDeviceName = 32;
            private const int CchFormName = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
            internal string DeviceName;

            internal short SpecVersion;
            internal short DriverVersion;
            internal short Size;
            internal short DriverExtra;
            internal uint Fields;

            internal int PositionX;
            internal int PositionY;
            internal int DisplayOrientation;
            internal int DisplayFixedOutput;

            internal short Color;
            internal short Duplex;
            internal short YResolution;
            internal short TrueTypeOption;
            internal short Collate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
            internal string FormName;

            internal short LogPixels;
            internal int BitsPerPel;
            internal int PelsWidth;
            internal int PelsHeight;
            internal int DisplayFlags;
            internal int DisplayFrequency;
            internal int IcmMethod;
            internal int IcmIntent;
            internal int MediaType;
            internal int DitherType;
            internal int Reserved1;
            internal int Reserved2;
            internal int PanningWidth;
            internal int PanningHeight;
        }

        internal const short DmSizeOf = 220;

        // ---------------------------------------------------------------- delegates

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        internal delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        // ---------------------------------------------------------------- user32

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "UnregisterClassW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "CreateWindowExW", SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "DefWindowProcW")]
        internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "UpdateWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateWindow(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(ref Message message, IntPtr window,
            uint minMessage, uint maxMessage, uint removeMessage);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "DispatchMessageW")]
        internal static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "PostQuitMessage")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetClientRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr window, ref Rect rectangle);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetWindowRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, ref Rect rectangle);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "AdjustWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustWindowRect(ref Rect rectangle, uint style,
            [MarshalAs(UnmanagedType.Bool)] bool menu);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ShowCursor")]
        internal static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetCapture")]
        internal static extern IntPtr SetCapture(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ReleaseCapture")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "LoadCursorW")]
        internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "LoadIconW")]
        internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetSystemMetrics")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetDC")]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ReleaseDC")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ClipCursor", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClipCursor(ref Rect rectangle);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ClipCursor", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClipCursorEmpty(IntPtr rectangle);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ClientToScreen", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr window, ref Point point);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "ScreenToClient", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(IntPtr window, ref Point point);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetForegroundWindow")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetWindowLong", SetLastError = true)]
        internal static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetWindowLong", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetKeyState")]
        internal static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetKeyNameTextW")]
        internal static extern int GetKeyNameText(int lParam, [Out] char[] buffer, int size);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MessageBeep")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MessageBeep(uint type);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MonitorFromWindow")]
        internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "ChangeDisplaySettingsW")]
        internal static extern int ChangeDisplaySettings(ref DevMode deviceMode, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode deviceMode);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetMessageW")]
        internal static extern int GetMessage(ref Message message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        // ---------------------------------------------------------------- kernel32

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "GetModuleHandleW", SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "GetCurrentProcess")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "QueryPerformanceCounter")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceCounter(out long counter);

        [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "QueryPerformanceFrequency")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceFrequency(out long frequency);

        [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "GetTickCount")]
        internal static extern uint GetTickCount();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            EntryPoint = "LoadLibraryW", SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "FreeLibrary")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "GetProcAddress")]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);

        // ---------------------------------------------------------------- winmm

        [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "timeBeginPeriod")]
        internal static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "timeEndPeriod")]
        internal static extern uint TimeEndPeriod(uint milliseconds);

        [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "timeGetTime")]
        internal static extern uint TimeGetTime();

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Resolves an entry point from a DLL that may not exist on this Windows
        /// version. Returns <see cref="IntPtr.Zero"/> when either the library or
        /// the function is missing, which is the normal case on XP.
        /// </summary>
        internal static IntPtr TryLoad(string library, string entryPoint)
        {
            var module = LoadLibrary(library);
            if (module == IntPtr.Zero) return IntPtr.Zero;

            var address = GetProcAddress(module, entryPoint);

            // Deliberately not freed: the module stays loaded for the process
            // lifetime, and freeing it here would invalidate the pointer.
            return address;
        }
    }
}
