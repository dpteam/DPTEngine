//
// DPT.Engine - Addict2 project import tests.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;

using DPT.Engine.Import;
using DPT.Engine.Project;

namespace DPT.Engine.Tests
{
    /// <summary>
    /// Tests the .dmo importer against the real projects that ship with the original
    /// Addict2 sources.
    ///
    /// This is the most valuable test in the repository. The .dmo format is a chunk
    /// stream where every record declares its own length, so a wrong struct size
    /// makes the reader consume the wrong number of bytes - and the importer checks
    /// that after every single chunk. Parsing nineteen real projects therefore
    /// verifies every field offset in the format at once, which no hand-written
    /// fixture could do.
    ///
    /// The reference projects live under refs/, which is not committed, so the test
    /// skips when they are absent rather than failing a fresh checkout.
    /// </summary>
    internal sealed class ImportTests
    {
        private static string ProjectDirectory()
        {
            // Walk up from the test binary to the repository root, then into refs.
            var directory = AppDomain.CurrentDomain.BaseDirectory;

            for (var i = 0; i < 8 && directory != null; i++)
            {
                var candidate = Path.Combine(directory, "refs", "Some2", "aDDict2", "Data", "projects");

                if (Directory.Exists(candidate)) return candidate;

                directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
            }

            return null;
        }

        private static string[] FindProjects()
        {
            var directory = ProjectDirectory();

            if (directory == null) return new string[0];

            var files = Directory.GetFiles(directory, "*.dmo");
            Array.Sort(files, StringComparer.Ordinal);

            return files;
        }

        [Test]
        internal void EveryOriginalProjectParses()
        {
            var files = FindProjects();

            if (files.Length == 0)
            {
                Console.WriteLine("        (no reference projects; skipping)");
                return;
            }

            var failures = new List<string>();

            foreach (var file in files)
            {
                try
                {
                    var project = DmoReader.Load(file);

                    if (project.Scenes.Count == 0)
                    {
                        failures.Add(Path.GetFileName(file) + ": no scenes");
                    }
                }
                catch (Exception error)
                {
                    // The importer's own chunk-length check produces a precise message
                    // naming the chunk and the byte counts, so surfacing it verbatim is
                    // what makes a failure actionable.
                    failures.Add(Path.GetFileName(file) + ": " + error.Message);
                }
            }

            if (failures.Count > 0)
            {
                var message = failures.Count + " of " + files.Length + " projects failed:";

                for (var i = 0; i < failures.Count && i < 6; i++)
                {
                    message += Environment.NewLine + "  " + failures[i];
                }

                throw new AssertionException(message);
            }

            Console.WriteLine("        parsed " + files.Length + " original projects");
        }

        [Test]
        internal void ImportedProjectHasGeometryAndTimeline()
        {
            var files = FindProjects();

            if (files.Length == 0)
            {
                Console.WriteLine("        (no reference projects; skipping)");
                return;
            }

            // The tutorial project is the smallest complete one: textures, scenes,
            // a world, a camera and a timeline.
            string tutorial = null;

            foreach (var file in files)
            {
                if (Path.GetFileName(file).IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tutorial = file;
                    break;
                }
            }

            if (tutorial == null) tutorial = files[0];

            var project = DmoReader.Load(tutorial);

            Assert.True(project.Scenes.Count > 0, "the project has scenes");

            var nodes = 0;
            var triangles = 0;

            for (var i = 0; i < project.Scenes.Count; i++)
            {
                var scene = project.Scenes[i];
                nodes += scene.Nodes.Count;

                for (var n = 0; n < scene.Nodes.Count; n++)
                {
                    if (scene.Nodes[n].Mesh != null) triangles += scene.Nodes[n].Mesh.TriangleCount;
                }
            }

            Assert.True(nodes > 0, "the project has scene nodes");
            Assert.True(triangles > 0, "the project has geometry, got " + triangles + " triangles");

            Console.WriteLine("        " + Path.GetFileName(tutorial) + ": " + project.Scenes.Count +
                              " scene(s), " + nodes + " node(s), " + triangles + " triangle(s), " +
                              project.Timeline.Count + " effect(s), " +
                              project.Textures.Count + " texture(s)");
        }

        [Test]
        internal void ImportedProjectSurvivesTheEngineFormat()
        {
            var files = FindProjects();

            if (files.Length == 0)
            {
                Console.WriteLine("        (no reference projects; skipping)");
                return;
            }

            var imported = DmoReader.Load(files[0]);

            // The whole point of the importer is that an original project becomes
            // something the engine can save and reload. If that round trip loses
            // anything, the conversion is not finished.
            var text = ProjectSerializer.ToText(imported);
            var reloaded = ProjectSerializer.LoadFromText(text);

            Assert.Equal(imported.Scenes.Count, reloaded.Scenes.Count, "scenes survive");
            Assert.Equal(imported.Timeline.Count, reloaded.Timeline.Count, "effects survive");
            Assert.Equal(imported.Textures.Count, reloaded.Textures.Count, "textures survive");
            Assert.Equal(imported.Duration, reloaded.Duration, "duration survives");
        }

        [Test]
        internal void RejectsFilesThatAreNotProjects()
        {
            Assert.Throws<InvalidDataException>(
                delegate { DmoReader.Load(new byte[64], "not-a-project"); },
                "a zero-filled buffer is not a project");
        }
    }
}
