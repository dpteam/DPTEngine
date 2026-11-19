//
// DPT.Engine - editor entry point.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Windows.Forms;

namespace DPT.Engine.Editor
{
    internal static class Program
    {
        /// <summary>
        /// Suppresses the Windows crash dialog so a failure leaves a message rather
        /// than a modal box on a machine nobody is watching.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpfaultErrorBox = 0x0002;
        private const uint SemNoOpenFileErrorBox = 0x8000;

        [STAThread]
        private static int Main(string[] args)
        {
            SetErrorMode(SemFailCriticalErrors | SemNoGpfaultErrorBox | SemNoOpenFileErrorBox);

            var projectPath = args.Length > 0 ? args[0] : null;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new EditorForm(projectPath));
                return 0;
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    "The editor could not start:\n\n" + error.Message,
                    "DPT.Engine Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return 1;
            }
        }
    }
}
