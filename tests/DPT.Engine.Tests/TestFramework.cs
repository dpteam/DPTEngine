//
// DPT.Engine - minimal test framework.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Reflection;

namespace DPT.Engine.Tests
{
    /// <summary>
    /// Marks a method as a test.
    ///
    /// A hand-written runner rather than a package: the tests have to run on
    /// .NET Framework 4.0 against the shipping assembly, and the usual test
    /// frameworks either need a newer runtime or a runner host that is itself a
    /// dependency. This is a hundred lines and it runs anywhere the engine does.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute
    {
    }

    /// <summary>Marks a test as expected to fail on a machine without 3D hardware.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class RequiresGraphicsAttribute : Attribute
    {
    }

    internal sealed class AssertionException : Exception
    {
        internal AssertionException(string message) : base(message)
        {
        }
    }

    internal static class Assert
    {
        internal static void True(bool condition, string message)
        {
            if (!condition) throw new AssertionException("Expected true: " + message);
        }

        internal static void False(bool condition, string message)
        {
            if (condition) throw new AssertionException("Expected false: " + message);
        }

        internal static void Equal(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new AssertionException(message + ": expected " + expected + ", got " + actual);
            }
        }

        internal static void Equal(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new AssertionException(message + ": expected '" + expected + "', got '" + actual + "'");
            }
        }

        internal static void Equal(uint expected, uint actual, string message)
        {
            if (expected != actual)
            {
                throw new AssertionException(message + ": expected 0x" + expected.ToString("X8") +
                                             ", got 0x" + actual.ToString("X8"));
            }
        }

        internal static void Near(float expected, float actual, float tolerance, string message)
        {
            var delta = Math.Abs(expected - actual);

            if (delta > tolerance || float.IsNaN(delta))
            {
                throw new AssertionException(message + ": expected " + expected + " +/- " + tolerance +
                                             ", got " + actual);
            }
        }

        internal static void NotNull(object value, string message)
        {
            if (value == null) throw new AssertionException("Expected non-null: " + message);
        }

        internal static void Null(object value, string message)
        {
            if (value != null) throw new AssertionException("Expected null: " + message);
        }

        internal static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception error)
            {
                throw new AssertionException(message + ": expected " + typeof(TException).Name +
                                             ", got " + error.GetType().Name + " (" + error.Message + ")");
            }

            throw new AssertionException(message + ": expected " + typeof(TException).Name + ", nothing was thrown");
        }
    }

    /// <summary>
    /// Discovers and runs every <see cref="TestAttribute"/> method in the
    /// assembly, in a stable order, and reports a summary.
    /// </summary>
    internal static class TestRunner
    {
        internal static int Run(string filter)
        {
            var types = new List<Type>(Assembly.GetExecutingAssembly().GetTypes());
            types.Sort(delegate(Type a, Type b) { return string.CompareOrdinal(a.Name, b.Name); });

            var passed = 0;
            var failed = 0;
            var skipped = 0;
            var failures = new List<string>();

            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Static | BindingFlags.Instance);

                Array.Sort(methods, delegate(MethodInfo a, MethodInfo b)
                {
                    return string.CompareOrdinal(a.Name, b.Name);
                });

                foreach (var method in methods)
                {
                    if (method.GetCustomAttributes(typeof(TestAttribute), false).Length == 0) continue;

                    var name = type.Name + "." + method.Name;

                    if (!string.IsNullOrEmpty(filter) &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        object instance = null;

                        if (!method.IsStatic)
                        {
                            instance = Activator.CreateInstance(type);
                        }

                        method.Invoke(instance, null);
                        passed++;
                        Console.WriteLine("  PASS  " + name);
                    }
                    catch (TargetInvocationException wrapped)
                    {
                        var error = wrapped.InnerException ?? wrapped;

                        if (error is GraphicsUnavailableException)
                        {
                            skipped++;
                            Console.WriteLine("  SKIP  " + name + "  (" + error.Message + ")");
                            continue;
                        }

                        failed++;
                        var text = error is AssertionException
                            ? error.Message
                            : error.GetType().Name + ": " + error.Message;

                        Console.WriteLine("  FAIL  " + name);
                        Console.WriteLine("        " + text.Replace("\n", "\n        "));

                        if (!(error is AssertionException) && error.StackTrace != null)
                        {
                            Console.WriteLine("        " + error.StackTrace.Split('\n')[0].Trim());
                        }

                        failures.Add(name + ": " + text);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + ", failed " + failed + ", skipped " + skipped);

            if (failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("failures:");
                foreach (var failure in failures)
                {
                    Console.WriteLine("  " + failure);
                }
            }

            return failed == 0 ? 0 : 1;
        }
    }

    /// <summary>Thrown when a test cannot run because the machine has no 3D device.</summary>
    internal sealed class GraphicsUnavailableException : Exception
    {
        internal GraphicsUnavailableException(string message) : base(message)
        {
        }
    }
}
