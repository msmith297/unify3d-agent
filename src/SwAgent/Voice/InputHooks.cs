using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SwAgent.Infrastructure;

namespace SwAgent.Voice
{
    /// <summary>
    /// Low-level keyboard and mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) on a thread of their own.
    /// Push-to-talk needs to know when a key goes up as well as down (a SolidWorks shortcut only fires on the press),
    /// and the sticky markup pen needs left-button drags over the graphics area while every other button still reaches
    /// SolidWorks (so the view can be rotated, panned and zoomed while drawing).
    ///
    /// Windows calls these synchronously for every input event on the machine and drops a hook that takes too long. On
    /// the SolidWorks UI thread every key press on the PC would wait whenever SolidWorks is busy (a rebuild, an agent's
    /// macro), so the hooks live on their own thread with its own message loop. Handlers run on that thread: they must
    /// return at once and touch nothing but thread-safe state - decide, record, and post the real work to the UI thread.
    /// </summary>
    internal sealed class InputHooks : IDisposable
    {
        /// <summary>Return true to swallow the key event. Arguments: virtual key, down (vs up), injected.</summary>
        public Func<int, bool, bool, bool> Key;

        /// <summary>Return true to swallow the mouse event. Arguments: message (WM_*), screen point, X button (1 or 2) for WM_XBUTTON*.</summary>
        public Func<int, Point, int, bool> Mouse;

        private readonly HookProc _keyProc;
        private readonly HookProc _mouseProc;
        private IntPtr _keyHook;
        private IntPtr _mouseHook;
        private readonly Control _thread; // the hook thread's invoker
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();

        public InputHooks()
        {
            _keyProc = KeyProc;
            _mouseProc = MouseProc;
            Control invoker = null;
            var t = new Thread(() =>
            {
                invoker = new Control();
                invoker.CreateControl();
                var _ = invoker.Handle;
                _ready.Set();
                Application.Run(); // the message loop the hooks are called from
            }) { IsBackground = true, Name = "input-hooks" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _ready.Wait(5000);
            _thread = invoker;
        }

        public void EnableKeyboard(bool on) => OnHookThread(() => SetKeyboard(on));

        public void EnableMouse(bool on) => OnHookThread(() => SetMouse(on));

        private void OnHookThread(Action a)
        {
            try
            {
                if (_thread != null && _thread.IsHandleCreated) _thread.BeginInvoke((MethodInvoker)(() => Safe.Run("Input hooks", a)));
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        private void SetKeyboard(bool on)
        {
            if (on && _keyHook == IntPtr.Zero)
            {
                _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, GetModuleHandle(null), 0);
                if (_keyHook == IntPtr.Zero) Log.Warn("Keyboard hook failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            else if (!on && _keyHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyHook);
                _keyHook = IntPtr.Zero;
            }
        }

        private void SetMouse(bool on)
        {
            if (on && _mouseHook == IntPtr.Zero)
            {
                _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
                if (_mouseHook == IntPtr.Zero) Log.Warn("Mouse hook failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            else if (!on && _mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        /// <summary>True when the foreground window belongs to SolidWorks (this process) - the panel included.</summary>
        public static bool SolidWorksInFront()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out var pid);
            return pid == Pid;
        }

        private static readonly uint Pid = (uint)Process.GetCurrentProcess().Id;

        public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>Type a key press (down and up) that a hook swallowed but that turned out to be meant for the app.</summary>
        public static void Replay(int vk)
        {
            var inputs = new[]
            {
                new Input { Type = 1, Ki = new KeybdInput { Vk = (ushort)vk } },
                new Input { Type = 1, Ki = new KeybdInput { Vk = (ushort)vk, Flags = 2 } }, // KEYEVENTF_KEYUP
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        }

        private IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Key != null)
            {
                try
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    var msg = wParam.ToInt32();
                    var down = msg == WmKeyDown || msg == WmSysKeyDown;
                    var up = msg == WmKeyUp || msg == WmSysKeyUp;
                    if ((down || up) && Key((int)info.VkCode, down, (info.Flags & 0x10) != 0)) return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Log.Warn("Keyboard hook handler failed: " + ex.Message);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Mouse != null)
            {
                try
                {
                    var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                    if ((info.Flags & 0x1) == 0 && Mouse(wParam.ToInt32(), new Point(info.X, info.Y), (int)(info.MouseData >> 16))) return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Log.Warn("Mouse hook handler failed: " + ex.Message);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        public void Dispose()
        {
            OnHookThread(() =>
            {
                SetKeyboard(false);
                SetMouse(false);
                Application.ExitThread();
            });
        }

        public const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
        public const int WmMouseMove = 0x200, WmLButtonDown = 0x201, WmLButtonUp = 0x202, WmRButtonDown = 0x204, WmRButtonUp = 0x205,
            WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmMouseWheel = 0x20A, WmXButtonDown = 0x20B, WmXButtonUp = 0x20C, WmMouseHWheel = 0x20E;

        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode, ScanCode, Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsLlHookStruct
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput
        {
            public ushort Vk, Scan;
            public uint Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Input
        {
            [FieldOffset(0)] public int Type;
            [FieldOffset(8)] public KeybdInput Ki;
            // MOUSEINPUT is the largest member of the union: keep the struct its size.
            [FieldOffset(8)] private readonly long _pad0;
            [FieldOffset(16)] private readonly long _pad1;
            [FieldOffset(24)] private readonly long _pad2;
            [FieldOffset(32)] private readonly long _pad3;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
