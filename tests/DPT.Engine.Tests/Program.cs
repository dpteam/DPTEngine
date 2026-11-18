//
// DPT.Engine - test entry point.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var filter = args.Length > 0 ? args[0] : null;

            Console.WriteLine("DPT.Engine tests");
            Console.WriteLine();

            return TestRunner.Run(filter);
        }
    }
}
