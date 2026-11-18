//
// DPT.Engine - keyboard and mouse state.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Text;

namespace DPT.Engine.Win32
{
    /// <summary>
    /// The input snapshot for one frame.
    ///
    /// The window pushes raw messages in; the game reads state out. Two details
    /// matter for a demo engine:
    ///
    /// * Edge state survives the whole frame. A key pressed and released inside
    ///   one 16 ms frame still reports <see cref="WasPressed"/>, because a
    ///   timeline trigger that misses a fast tap is a bug the user sees.
    ///
    /// * Nothing allocates per frame. The arrays are fixed at 256 entries and
    ///   the character buffer is reused, so the input path does not add garbage
    ///   to a 60 Hz loop.
    /// </summary>
    public sealed class InputState
    {
        private readonly bool[] down = new bool[256];
        private readonly bool[] pressed = new bool[256];
        private readonly bool[] released = new bool[256];

        private readonly StringBuilder text = new StringBuilder(32);

        private MouseButtons buttonsDown;
        private MouseButtons buttonsPressed;
        private MouseButtons buttonsReleased;

        private int mouseX;
        private int mouseY;
        private int deltaX;
        private int deltaY;
        private int wheel;

        /// <summary>True while the window has focus; input is ignored otherwise.</summary>
        public bool HasFocus { get; internal set; }

        public int MouseX { get { return mouseX; } }
        public int MouseY { get { return mouseY; } }
        public int MouseDeltaX { get { return deltaX; } }
        public int MouseDeltaY { get { return deltaY; } }

        /// <summary>Wheel movement in notches; positive is away from the user.</summary>
        public int Wheel { get { return wheel; } }

        public MouseButtons ButtonsDown { get { return buttonsDown; } }

        // ---------------------------------------------------------------- queries

        public bool IsDown(Keys key)
        {
            var index = (int)key;
            return index >= 0 && index < 256 && down[index];
        }

        public bool WasPressed(Keys key)
        {
            var index = (int)key;
            return index >= 0 && index < 256 && pressed[index];
        }

        public bool WasReleased(Keys key)
        {
            var index = (int)key;
            return index >= 0 && index < 256 && released[index];
        }

        public bool IsDown(MouseButtons button)
        {
            return (buttonsDown & button) != 0;
        }

        public bool WasPressed(MouseButtons button)
        {
            return (buttonsPressed & button) != 0;
        }

        public bool WasReleased(MouseButtons button)
        {
            return (buttonsReleased & button) != 0;
        }

        /// <summary>True when either Shift key is held.</summary>
        public bool Shift
        {
            get { return IsDown(Keys.Shift) || IsDown(Keys.LeftShift) || IsDown(Keys.RightShift); }
        }

        public bool Control
        {
            get { return IsDown(Keys.Control) || IsDown(Keys.LeftControl) || IsDown(Keys.RightControl); }
        }

        public bool Alt
        {
            get { return IsDown(Keys.Alt) || IsDown(Keys.LeftAlt) || IsDown(Keys.RightAlt); }
        }

        // ---------------------------------------------------------------- text

        /// <summary>Characters typed this frame, already filtered of control codes.</summary>
        public string TypedText
        {
            get { return text.ToString(); }
        }

        public bool HasTypedText
        {
            get { return text.Length > 0; }
        }

        // ---------------------------------------------------------------- feeding

        internal void OnKeyDown(int virtualKey)
        {
            if (virtualKey < 0 || virtualKey > 255) return;

            // Auto-repeat sends repeated WM_KEYDOWN. Only the first one is an edge,
            // otherwise "press R to reload" fires at the keyboard repeat rate.
            if (!down[virtualKey])
            {
                pressed[virtualKey] = true;
            }

            down[virtualKey] = true;

            // Windows reports Shift as VK_SHIFT with a scancode; mirror it onto the
            // side-specific codes so bindings can distinguish left from right.
            MirrorModifiers(virtualKey, true);
        }

        internal void OnKeyUp(int virtualKey)
        {
            if (virtualKey < 0 || virtualKey > 255) return;

            if (down[virtualKey])
            {
                released[virtualKey] = true;
            }

            down[virtualKey] = false;
            MirrorModifiers(virtualKey, false);
        }

        private void MirrorModifiers(int virtualKey, bool isDown)
        {
            switch (virtualKey)
            {
                case (int)Keys.Shift:
                    SetKey(Keys.LeftShift, isDown);
                    break;
                case (int)Keys.Control:
                    SetKey(Keys.LeftControl, isDown);
                    break;
                case (int)Keys.Alt:
                    SetKey(Keys.LeftAlt, isDown);
                    break;
            }
        }

        private void SetKey(Keys key, bool isDown)
        {
            var index = (int)key;
            if (index < 0 || index > 255) return;

            if (isDown && !down[index]) pressed[index] = true;
            if (!isDown && down[index]) released[index] = true;

            down[index] = isDown;
        }

        internal void OnChar(char character)
        {
            // Control characters are handled as keys, not text; letting them into
            // the buffer makes a text field move its caret on Ctrl+arrow.
            if (character < 0x20 || character == 0x7F) return;
            if (text.Length >= 256) return;

            text.Append(character);
        }

        internal void OnMouseMove(int x, int y)
        {
            deltaX += x - mouseX;
            deltaY += y - mouseY;
            mouseX = x;
            mouseY = y;
        }

        internal void OnButtonDown(MouseButtons button)
        {
            if ((buttonsDown & button) == 0)
            {
                buttonsPressed |= button;
            }

            buttonsDown |= button;
        }

        internal void OnButtonUp(MouseButtons button)
        {
            if ((buttonsDown & button) != 0)
            {
                buttonsReleased |= button;
            }

            buttonsDown &= ~button;
        }

        internal void OnWheel(int notches)
        {
            wheel += notches;
        }

        /// <summary>Drops everything, used when the window loses focus.</summary>
        internal void Reset()
        {
            for (var i = 0; i < 256; i++)
            {
                if (down[i]) released[i] = true;
                down[i] = false;
            }

            buttonsReleased |= buttonsDown;
            buttonsDown = MouseButtons.None;
            text.Length = 0;
        }

        internal void SetMousePosition(int x, int y)
        {
            mouseX = x;
            mouseY = y;
            deltaX = 0;
            deltaY = 0;
        }

        /// <summary>
        /// Called once per frame, after the game has read the state: clears the
        /// edges and the per-frame accumulators.
        /// </summary>
        internal void EndFrame()
        {
            for (var i = 0; i < 256; i++)
            {
                pressed[i] = false;
                released[i] = false;
            }

            buttonsPressed = MouseButtons.None;
            buttonsReleased = MouseButtons.None;
            deltaX = 0;
            deltaY = 0;
            wheel = 0;
            text.Length = 0;
        }
    }
}
