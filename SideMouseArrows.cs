using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

static class SideMouseArrows
{
    const int WhMouseLl = 14;
    const int WhKeyboardLl = 13;
    const int WmXbuttonDown = 0x020B;
    const int WmXbuttonUp = 0x020C;
    const int WmXbuttonDblClk = 0x020D;
    const int WmKeyDown = 0x0100;
    const int WmKeyUp = 0x0101;
    const int WmSysKeyDown = 0x0104;
    const int WmSysKeyUp = 0x0105;
    const int Xbutton1 = 1;
    const int Xbutton2 = 2;
    const int VkLeft = 0x25;
    const int VkRight = 0x27;
    const int VkBrowserBack = 0xA6;
    const int VkBrowserForward = 0xA7;
    const uint KeyeventfExtendedkey = 0x0001;
    const uint KeyeventfKeyup = 0x0002;
    const uint WmAppHold = 0x8001;

    static IntPtr _mouseHook;
    static IntPtr _keyHook;
    static HookProc _mouseProc;
    static HookProc _keyProc;
    static bool _rightHeld;
    static bool _leftHeld;
    static uint _threadId;
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideMouseArrows",
        "log.txt");

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MsllHookStruct
    {
        public int x;
        public int y;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KbdllHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
        public int lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    const uint MouseeventfHwheel = 0x01000;
    static Timer _rightWheel;
    static Timer _leftWheel;

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    static readonly Dictionary<IntPtr, bool> RazerCache = new Dictionary<IntPtr, bool>();
    static int _learnedRight;
    static int _learnedLeft;
    static string _lastHid = "";

    static int Main()
    {
        bool created;
        using (var mutex = new Mutex(true, @"Local\SideMouseArrows", out created))
        {
            if (!created)
                return 0;

            _threadId = GetCurrentThreadId();
            _mouseProc = MouseCallback;
            _keyProc = KeyCallback;
            RuntimeHelpers.PrepareMethod(_mouseProc.Method.MethodHandle);
            RuntimeHelpers.PrepareMethod(_keyProc.Method.MethodHandle);
            RuntimeHelpers.PrepareMethod(typeof(SideMouseArrows).GetMethod("SendKey", BindingFlags.Static | BindingFlags.NonPublic).MethodHandle);

            var module = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, module, 0);
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            {
                Log("hook failed " + Marshal.GetLastWin32Error());
                return Marshal.GetLastWin32Error();
            }

            Log("started");
            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };
            LoadLearned();
            IntPtr hwnd = CreateWindowEx(0, "Static", "SideMouseArrows", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var devices = new RawInputDevice[2];
            devices[0].UsagePage = 0x01;
            devices[0].Usage = 0x06;
            devices[0].Flags = 0x100;
            devices[0].Target = hwnd;
            devices[1].UsagePage = 0x0C;
            devices[1].Usage = 0x01;
            devices[1].Flags = 0x100;
            devices[1].Target = hwnd;
            bool registered = RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
            Log("raw " + registered + " hwnd " + hwnd.ToInt64() + " err " + Marshal.GetLastWin32Error());

            Msg msg;
            PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);
            int pump;
            while ((pump = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
            {
                if (msg.message == WmAppHold)
                    Hold(msg.wParam.ToInt32(), msg.lParam != IntPtr.Zero);
                else if (msg.message == 0x00FF)
                    OnRawInput(msg.lParam);
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Log("loop end " + pump + " err " + Marshal.GetLastWin32Error());
            Shutdown();
            GC.KeepAlive(mutex);
            return 0;
        }
    }

    static void Shutdown()
    {
        if (_rightHeld)
        {
            _rightHeld = false;
            SendKey(VkRight, true);
        }
        if (_leftHeld)
        {
            _leftHeld = false;
            SendKey(VkLeft, true);
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
    }

    static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message == WmXbuttonDown || message == WmXbuttonUp || message == WmXbuttonDblClk)
            {
                var data = (MsllHookStruct)Marshal.PtrToStructure(lParam, typeof(MsllHookStruct));
                int button = (data.mouseData >> 16) & 0xFFFF;
                if (button == Xbutton1 || button == Xbutton2)
                {
                    int vk = button == Xbutton1 ? VkRight : VkLeft;
                    bool down = message == WmXbuttonDown;
                    Log("xbutton " + button + (down ? " down" : message == WmXbuttonUp ? " up" : " dbl"));
                    if (message != WmXbuttonDblClk)
                    {
                        bool posted = PostThreadMessage(_threadId, WmAppHold, (IntPtr)vk, down ? (IntPtr)1 : IntPtr.Zero);
                        if (!posted)
                            Log("post failed " + Marshal.GetLastWin32Error());
                    }
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    static IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message == WmKeyDown || message == WmKeyUp || message == WmSysKeyDown || message == WmSysKeyUp)
            {
                var data = (KbdllHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdllHookStruct));
                int vk = (int)data.vkCode;
                if (vk == VkBrowserBack || vk == VkBrowserForward)
                {
                    int arrow = vk == VkBrowserBack ? VkRight : VkLeft;
                    bool down = message == WmKeyDown || message == WmSysKeyDown;
                    Log("browser " + vk.ToString("X") + (down ? " down" : " up"));
                    PostThreadMessage(_threadId, WmAppHold, (IntPtr)arrow, down ? (IntPtr)1 : IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    static void Hold(int vk, bool down)
    {
        if (vk == VkRight)
        {
            if (down)
            {
                if (_rightHeld)
                    return;
                _rightHeld = true;
            }
            else
            {
                if (!_rightHeld)
                    return;
                _rightHeld = false;
            }
        }
        else
        {
            if (down)
            {
                if (_leftHeld)
                    return;
                _leftHeld = true;
            }
            else
            {
                if (!_leftHeld)
                    return;
                _leftHeld = false;
            }
        }
        int delta = vk == VkRight ? 40 : -40;
        if (down)
        {
            SendWheel(delta);
            var timer = new Timer(delegate { SendWheel(delta); }, null, 70, 70);
            if (vk == VkRight)
                _rightWheel = timer;
            else
                _leftWheel = timer;
        }
        else
        {
            Timer timer = vk == VkRight ? _rightWheel : _leftWheel;
            if (vk == VkRight)
                _rightWheel = null;
            else
                _leftWheel = null;
            if (timer != null)
                timer.Dispose();
        }
        Log("scroll " + (vk == VkRight ? "right" : "left") + (down ? " down" : " up"));
    }

    static void SendWheel(int delta)
    {
        mouse_event(MouseeventfHwheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    static void SendKey(int vk, bool up)
    {
        uint flags = KeyeventfExtendedkey;
        if (up)
            flags |= KeyeventfKeyup;
        keybd_event((byte)vk, (byte)MapVirtualKey((uint)vk, 0), flags, UIntPtr.Zero);
    }

    static void OnRawInput(IntPtr raw)
    {
        uint size = 0;
        int header = 24;
        GetRawInputData(raw, 0x10000003, IntPtr.Zero, ref size, (uint)header);
        if (size < (uint)header || size > 512)
            return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(raw, 0x10000003, buf, ref size, (uint)header) == 0xFFFFFFFF)
                return;
            int type = Marshal.ReadInt32(buf, 0);
            IntPtr device = Marshal.ReadIntPtr(buf, 8);
            if (!IsRazer(device))
                return;
            if (type == 1)
            {
                int vk = Marshal.ReadInt16(buf, header + 6) & 0xFFFF;
                int flags = Marshal.ReadInt16(buf, header + 2) & 0xFFFF;
                bool down = (flags & 1) == 0;
                Log("razer key " + vk.ToString("X") + (down ? " down" : " up"));
                if (vk != 0 && vk != VkBrowserBack && vk != VkBrowserForward)
                    LearnAndHold(vk, down);
            }
            else if (type == 2 && size > 32)
            {
                var hex = "";
                int n = (int)size - 32;
                if (n > 12)
                    n = 12;
                for (int i = 0; i < n; i++)
                    hex += Marshal.ReadByte(buf, 32 + i).ToString("X2");
                if (hex != _lastHid)
                {
                    _lastHid = hex;
                    Log("razer hid " + hex);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    static bool IsRazer(IntPtr device)
    {
        bool known;
        if (RazerCache.TryGetValue(device, out known))
            return known;
        uint size = 0;
        GetRawInputDeviceInfo(device, 0x20000007, IntPtr.Zero, ref size);
        known = false;
        if (size > 0 && size < 1024)
        {
            IntPtr name = Marshal.AllocHGlobal((int)size * 2);
            if (GetRawInputDeviceInfo(device, 0x20000007, name, ref size) != 0xFFFFFFFF)
            {
                string text = Marshal.PtrToStringUni(name);
                known = text != null && text.IndexOf("VID_1532", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            Marshal.FreeHGlobal(name);
        }
        RazerCache[device] = known;
        return known;
    }

    static void LearnAndHold(int vk, bool down)
    {
        int arrow = 0;
        if (vk == _learnedRight)
            arrow = VkRight;
        else if (vk == _learnedLeft)
            arrow = VkLeft;
        else if (_learnedRight == 0)
        {
            _learnedRight = vk;
            arrow = VkRight;
            SaveLearned();
        }
        else if (_learnedLeft == 0 && vk != _learnedRight)
        {
            _learnedLeft = vk;
            arrow = VkLeft;
            SaveLearned();
        }
        if (arrow != 0)
            Hold(arrow, down);
    }

    static string LearnPath
    {
        get { return Path.Combine(Path.GetDirectoryName(LogPath), "buttons.txt"); }
    }

    static void LoadLearned()
    {
        try
        {
            if (!File.Exists(LearnPath))
                return;
            string[] lines = File.ReadAllLines(LearnPath);
            if (lines.Length > 0)
                _learnedRight = int.Parse(lines[0]);
            if (lines.Length > 1)
                _learnedLeft = int.Parse(lines[1]);
        }
        catch
        {
        }
    }

    static void SaveLearned()
    {
        try
        {
            File.WriteAllLines(LearnPath, new string[] { _learnedRight.ToString(), _learnedLeft.ToString() });
        }
        catch
        {
        }
    }

    static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
