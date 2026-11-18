//
// DPT.Engine - vtable slot verification.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

using DPT.Engine.Graphics;

namespace DPT.Engine.SelfTest
{
    /// <summary>
    /// Determines the real vtable slots of the Direct3D 9 resource interfaces by
    /// calling them, instead of trusting a hand-copied table.
    ///
    /// This exists because a wrong slot does not fail loudly. It calls a different
    /// method with the wrong arguments: sometimes it returns an error, sometimes it
    /// writes a struct of the wrong size over the caller's stack, and sometimes it
    /// returns a plausible-looking pointer that crashes a thousand frames later.
    /// That is exactly how the engine's first render-target bug behaved.
    ///
    /// The probe uses only calls that cannot corrupt memory when wrong:
    ///
    ///   GetLevelCount  - takes only 'this', returns a DWORD. A 1-level texture
    ///                    returns 1; a 4-level texture returns 4.
    ///   GetLevelDesc   - writes a D3DSURFACE_DESC; verified against the known
    ///                    width and height of a texture the probe created.
    ///
    /// Once GetLevelCount is found, the rest of the interface follows from the
    /// header's method order, and the probe confirms each one.
    /// </summary>
    internal static class VtableProbe
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetLevelCountDelegate(IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetLevelDescDelegate(IntPtr texture, uint level, ref D3DSurfaceDescription description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSurfaceDescDelegate(IntPtr surface, ref D3DSurfaceDescription description);

        internal static void Run()
        {
            Console.WriteLine();
            Console.WriteLine("vtable probe");
            Console.WriteLine();

            using (var window = new Win32.Win32Window())
            {
                window.CreateHidden("DPT.Engine vtable probe", 64, 64);

                using (var device = new D3D9Device())
                {
                    device.Create(window.Handle, 64, 64, false, false);

                    ProbeTexture(device);
                    ProbeSurface(device);
                }
            }
        }

        private static void ProbeTexture(D3D9Device device)
        {
            const int width = 32;
            const int height = 16;
            const int levels = 4;

            using (var texture = device.CreateTexture(width, height, levels, false))
            {
                Console.WriteLine("  texture " + width + "x" + height + ", " + levels + " levels");

                // GetLevelCount is the first method of IDirect3DBaseTexture9. It
                // takes only 'this', so a wrong slot returns a harmless garbage
                // DWORD rather than corrupting anything.
                for (var slot = 8; slot <= 16; slot++)
                {
                    uint count;

                    try
                    {
                        var method = ComVtable.Method(texture.Handle, slot);
                        var call = (GetLevelCountDelegate)Marshal.GetDelegateForFunctionPointer(
                            method, typeof(GetLevelCountDelegate));

                        count = call(texture.Handle);
                    }
                    catch
                    {
                        continue;
                    }

                    var marker = count == levels ? "  <-- GetLevelCount" : "";
                    Console.WriteLine("    slot " + slot + ": returns " + count + marker);
                }

                // GetLevelDesc writes a 20-byte D3DSURFACE_DESC. Verify the width
                // and height, which the probe knows, so a wrong slot that happens
                // to write something is still rejected.
                for (var slot = 12; slot <= 20; slot++)
                {
                    var description = new D3DSurfaceDescription();

                    try
                    {
                        var method = ComVtable.Method(texture.Handle, slot);
                        var call = (GetLevelDescDelegate)Marshal.GetDelegateForFunctionPointer(
                            method, typeof(GetLevelDescDelegate));

                        var result = call(texture.Handle, 0, ref description);

                        if (result != 0) continue;

                        var marker = description.Width == width && description.Height == height
                            ? "  <-- GetLevelDesc"
                            : "";

                        Console.WriteLine("    slot " + slot + ": desc " + description.Width + "x" +
                                          description.Height + " format " + description.Format + marker);
                    }
                    catch
                    {
                        // A slot with a different signature throws or faults; both
                        // mean "not GetLevelDesc".
                    }
                }
            }
        }

        private static void ProbeSurface(D3D9Device device)
        {
            using (var target = device.CreateRenderTarget(48, 24))
            {
                Console.WriteLine("  render target 48x24, surface 0x" + target.Handle.ToString("X"));

                // Only GetDesc is safe to probe. It takes (this, D3DSURFACE_DESC*)
                // with no level argument - a different signature from the texture's
                // GetLevelDesc - and passing the wrong one writes the struct
                // pointer into the wrong register, which faults.
                for (var slot = 11; slot <= 13; slot++)
                {
                    var description = new D3DSurfaceDescription();

                    try
                    {
                        var method = ComVtable.Method(target.Handle, slot);
                        var call = (GetSurfaceDescDelegate)Marshal.GetDelegateForFunctionPointer(
                            method, typeof(GetSurfaceDescDelegate));

                        var result = call(target.Handle, ref description);

                        if (result != 0) continue;

                        var marker = description.Width == 48 && description.Height == 24
                            ? "  <-- GetDesc"
                            : "";

                        Console.WriteLine("    slot " + slot + ": desc " + description.Width + "x" +
                                          description.Height + marker);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
