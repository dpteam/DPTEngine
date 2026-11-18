//
// DPT.Engine - shared branding.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

namespace DPT.Engine.Shared
{
    using System;

    /// <summary>
    /// The engine's name, version and colours, in one place.
    ///
    /// Shared because the same strings appear in five programs (runtime, launcher,
    /// editor host, master and relay) and in four kinds of file: C# source, HTML,
    /// CSS and the Windows version resource. A fork that renames the engine should
    /// not have to hunt for the old name in a web page.
    /// </summary>
    public static class Branding
    {
        /// <summary>The public name, as it appears in window titles and on screen.</summary>
        public const string Name = "DPT.Engine";

        /// <summary>The version, without a leading 'v'.</summary>
        public const string Version = "1.0.0";

        /// <summary>Name and version together, for banners and About screens.</summary>
        public const string Title = Name + " v" + Version;

        /// <summary>What the project is, in one line.</summary>
        public const string Tagline = "A Win32 + Direct3D 9 demo engine with a web editor";

        // ---------------------------------------------------------------- palette
        //
        // Light is settled: the brand is a blue that reads as "engine", not as
        // "IDE", and white behind it so screenshots and docs stay legible when
        // printed. The two blues are one step apart on purpose - Primary is the
        // accent, Primary2 is the same hue one notch down for pressed states and
        // for text on white, where #0088FF alone is just short of AA contrast.
        public const string LightPrimary = "#0088FF";
        public const string LightPrimary2 = "#0080FF";
        public const string LightBackground = "#FFFFFF";
        public const string LightSurface = "#F2F6FB";
        public const string LightText = "#0A1220";

        // Dark is provisional. #000088 is the intended accent, but a saturated
        // navy at that value is nearly invisible on black, so the actual accent
        // here is lifted for legibility. Replace these two the moment a real
        // dark design exists - nothing else in the codebase hardcodes them.
        public const string DarkPrimary = "#000088";
        public const string DarkPrimary2 = "#0000AA";
        public const string DarkBackground = "#000000";
        public const string DarkSurface = "#07070F";
        public const string DarkText = "#E8ECF5";

        // OLED is not designed yet. Kept as a named slot so the editor's theme
        // switch has somewhere to point, and so a future pass does not have to
        // invent a third set of constants under pressure.
        public const string OledPrimary = "#0088FF";
        public const string OledPrimary2 = "#0080FF";
        public const string OledBackground = "#000000";
        public const string OledSurface = "#000000";
        public const string OledText = "#FFFFFF";

        /// <summary>Alias kept for the light theme, which is the default.</summary>
        public const string PrimaryColor = LightPrimary;

        public const string SecondaryColor = LightPrimary2;
        public const string DarkColor = LightBackground;
        public const string OnDarkColor = LightText;

        /// <summary>Where the project lives. Empty when a fork has no home to point at.</summary>
        public const string HomeUrl = "https://github.com/dpteam/DPTEngine";

        public const string RuntimeName = Name;
        public const string LauncherName = Name + " Launcher";
        public const string EditorName = Name + " Editor";
        public const string MasterName = Name + " master";

        /// <summary>File name of the runtime executable, without extension.</summary>
        public const string RuntimeExecutable = "dptengine";

        public const string LauncherExecutable = "dptengine-launcher";
        public const string EditorExecutable = "dptengine-editor";
        public const string MasterExecutable = "dptengine-master";

        /// <summary>
        /// Directory name used for user data when the install folder is not
        /// writable (Vista and later) - under %APPDATA%.
        /// </summary>
        public const string DataFolderName = "DPTEngine";

        // ---------------------------------------------------------------- themes

        /// <summary>
        /// The three themes the UI can be in.
        ///
        /// Only Light is a finished design. Dark exists because a demo engine is
        /// used in dark rooms and #000088 on #000000 is the intended look; OLED
        /// is a placeholder so the switch has a third position and nobody has to
        /// invent one under deadline.
        /// </summary>
        public enum Theme
        {
            Light = 0,
            Dark = 1,
            Oled = 2
        }

        /// <summary>
        /// Palette values for a theme, in the order the CSS expects them:
        /// primary, primary-2, background, surface, text, and the on-primary
        /// colour for buttons.
        /// </summary>
        public static void GetPalette(Theme theme, out string primary, out string primary2,
            out string background, out string surface, out string text, out string onPrimary)
        {
            switch (theme)
            {
                case Theme.Dark:
                    primary = DarkPrimary;
                    primary2 = DarkPrimary2;
                    background = DarkBackground;
                    surface = DarkSurface;
                    text = DarkText;
                    onPrimary = "#FFFFFF";
                    break;

                case Theme.Oled:
                    primary = OledPrimary;
                    primary2 = OledPrimary2;
                    background = OledBackground;
                    surface = OledSurface;
                    text = OledText;
                    onPrimary = "#FFFFFF";
                    break;

                default:
                    primary = LightPrimary;
                    primary2 = LightPrimary2;
                    background = LightBackground;
                    surface = LightSurface;
                    text = LightText;
                    onPrimary = "#FFFFFF";
                    break;
            }
        }

        public static string ThemeName(Theme theme)
        {
            switch (theme)
            {
                case Theme.Dark: return "dark";
                case Theme.Oled: return "oled";
                default: return "light";
            }
        }

        public static Theme ParseTheme(string name)
        {
            if (string.Equals(name, "dark", StringComparison.OrdinalIgnoreCase)) return Theme.Dark;
            if (string.Equals(name, "oled", StringComparison.OrdinalIgnoreCase)) return Theme.Oled;
            return Theme.Light;
        }
    }
}
