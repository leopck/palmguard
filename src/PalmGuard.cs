// PalmGuard - MacBook-style palm rejection for Windows Precision Touchpads.
//
// Architecture
//   Input thread (above-normal priority, message-only window):
//     raw PTP + keyboard input -> PG.Engine -> blocking decision
//     WH_MOUSE_LL hook swallows touchpad-driven events while a trusted contact is palm/pending
//     click repair (thumb-zone taps, recovery of presses Windows dropped)
//     core tick: block timeout, gesture hold, heat map decay, hook watchdog, self-training trigger
//   SPI worker thread: toggles pan/zoom gestures (non-persistent) while palms are present
//   UI thread: tray icon + menu, on-demand dashboard (draws from snapshots, never blocks input)
//
// Safety: fail-open everywhere (any exception -> input passes through), gestures restored and any
// injected button released on exit/crash, single instance, hook watchdog, sleep/resume + device changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using PG;

[assembly: AssemblyTitle("PalmGuard")]
[assembly: AssemblyProduct("PalmGuard")]
[assembly: AssemblyDescription("Palm rejection for Windows Precision Touchpads")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

// ======================================================================================
// Settings
// ======================================================================================
public class Settings
{
    public bool Enabled = true, Learning = true, ThumbTapIgnore = true, ClickRecovery = true, GestureHold = true, RecordSessions = true;
    public bool MacScroll = false, Momentum = true, ScrollReverse = false, TwoFingerClick = true;
    public int MaxSessions = 20, MaxSessionMB = 300, ScrollSpeed = 100;
    string path;

    public static Settings Load(string path)
    {
        var s = new Settings { path = path };
        try
        {
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var kv = line.Split(new[] { '=' }, 2); if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim(); bool b = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); int n; int.TryParse(v, out n);
                    switch (k)
                    {
                        case "Enabled": s.Enabled = b; break;
                        case "Learning": s.Learning = b; break;
                        case "ThumbTapIgnore": s.ThumbTapIgnore = b; break;
                        case "ClickRecovery": s.ClickRecovery = b; break;
                        case "GestureHold": s.GestureHold = b; break;
                        case "RecordSessions": s.RecordSessions = b; break;
                        case "MacScroll": s.MacScroll = b; break;
                        case "Momentum": s.Momentum = b; break;
                        case "ScrollReverse": s.ScrollReverse = b; break;
                        case "TwoFingerClick": s.TwoFingerClick = b; break;
                        case "ScrollSpeed": if (n >= 20 && n <= 500) s.ScrollSpeed = n; break;
                        case "MaxSessions": if (n > 0) s.MaxSessions = n; break;
                        case "MaxSessionMB": if (n > 0) s.MaxSessionMB = n; break;
                    }
                }
        }
        catch { }
        return s;
    }
    public void Save()
    {
        try
        {
            File.WriteAllLines(path, new[] {
                "# PalmGuard settings (1 = on, 0 = off)",
                "Enabled=" + (Enabled ? 1 : 0), "Learning=" + (Learning ? 1 : 0), "ThumbTapIgnore=" + (ThumbTapIgnore ? 1 : 0),
                "ClickRecovery=" + (ClickRecovery ? 1 : 0), "GestureHold=" + (GestureHold ? 1 : 0), "RecordSessions=" + (RecordSessions ? 1 : 0),
                "MacScroll=" + (MacScroll ? 1 : 0), "Momentum=" + (Momentum ? 1 : 0), "ScrollReverse=" + (ScrollReverse ? 1 : 0), "ScrollSpeed=" + ScrollSpeed,
                "TwoFingerClick=" + (TwoFingerClick ? 1 : 0),
                "MaxSessions=" + MaxSessions, "MaxSessionMB=" + MaxSessionMB });
        }
        catch { }
    }
}

// ======================================================================================
// Win32
// ======================================================================================
static class W32
{
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort Page; public ushort Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint sz);
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint n, uint sz);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] public static extern uint GetRawInputDeviceInfo(IntPtr h, uint c, IntPtr d, ref uint sz);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint b, byte[] c, uint d);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint val, IntPtr pp, byte[] rpt, uint len);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] list, ref uint len, IntPtr pp, byte[] rpt, uint len2);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    public const int HIDP_OK = 0x110000, WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_HOTKEY = 0x0312, WM_APP = 0x8000;
}

// ======================================================================================
// Core: engine + input processing + hook + gestures + click repair + learning
// ======================================================================================
static class Core
{
    public const string VERSION = "1.1.0";
    public const double HOLD_AFTER = 0.05, GESTURE_HOLD = 0.15;
    const double THUMB_TAP_MAX = 0.20, TAP_CLICK_WINDOW = 0.35, RECOVER_AFTER = 0.06;
    static readonly IntPtr INJECT_TAG = (IntPtr)0x50474D, PROBE_TAG = (IntPtr)0x504750; // "PGM", "PGP"

    public static readonly object Sync = new object();
    public static Engine eng = new Engine();
    public static Gestures gest = new Gestures();
    public static Learner learner;
    public static Model seedModel;
    public static Settings settings;
    public static string dataDir, sessionDir, seedDir;
    public static bool replay;
    public static Action<string> Notify = s => { };

    // device
    static Dictionary<IntPtr, bool> isPad = new Dictionary<IntPtr, bool>();
    static IntPtr padPP = IntPtr.Zero, padHandle = IntPtr.Zero; static List<ushort> links = new List<ushort>();
    public static string padName = ""; public static bool padDetected;

    // clock
    static Stopwatch sw = Stopwatch.StartNew();
    public static double replayT = -1;
    public static double Now() { return replayT >= 0 ? replayT : sw.Elapsed.TotalSeconds; }

    // decision state (read by hook without locking)
    public static volatile bool blocking;
    static double blockUntil = -1, gestureUntil = -1;
    public static string lastReason = "";
    static bool lDownSwallowed, rDownSwallowed;
    public static int blockedEvents, gestureOffCount, thumbTapsIgnored, clicksRecovered, hookReinstalls, errors;

    // click repair
    static bool rawButton, swallowTapUp, pendingPress, rightFromTwo;
    static uint injectedUp;                 // 0, or the button-up flag owed for a button we injected
    static bool injectedDown { get { return injectedUp != 0; } }
    public static int twoFingerClicks;
    static double lastThumbTapT = -9, padDownT = -9, lastPassedDownT = -9;

    // hook
    static W32.HookProc hookRef; static IntPtr hook;
    static long lastHookTicks; static int motionSinceHook; static bool probing, probeOk; static double probeT = -99, lastProbeT = -99;

    // logging
    static StreamWriter log = StreamWriter.Null, sessTouch = StreamWriter.Null, sessKeys = StreamWriter.Null;
    static readonly object logLock = new object();
    static int errorLogCount;

    // visuals (written by input thread, read by UI; benign races)
    public const int GW = 92, GH = 54, TL = 450;
    public static float[,] heatPalm = new float[GW, GH], heatFinger = new float[GW, GH];
    public static byte[] tlState = new byte[TL]; public static bool[] tlKey = new bool[TL]; public static int tlPos;
    static double lastKeyDrawn = -99;

    public static void Log(string ev, string detail)
    {
        lock (logLock) { try { log.WriteLine("{0:F4},{1},{2}", Now(), ev, (detail ?? "").Replace(',', ';').Replace('\n', ' ')); } catch { } }
    }
    public static void Error(string where, Exception e)
    {
        errors++;
        Log("error", where + ": " + e.Message);
        if (errorLogCount++ > 200 || dataDir == null) return;
        try
        {
            var p = Path.Combine(dataDir, "palmguard.log");
            if (File.Exists(p) && new FileInfo(p).Length > 5000000) File.Delete(p);
            File.AppendAllText(p, DateTime.Now.ToString("s") + " [" + where + "] " + e + Environment.NewLine);
        }
        catch { }
    }

    // ---------------------------------------------------------------- startup
    public static void Init(string dir, bool isReplay)
    {
        dataDir = dir; replay = isReplay; Directory.CreateDirectory(dataDir);
        settings = isReplay ? new Settings() : Settings.Load(Path.Combine(dataDir, "settings.ini"));
        if (!isReplay) settings.Save();
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        seedDir = Directory.Exists(Path.Combine(exeDir, "model")) ? Path.Combine(exeDir, "model") : Path.GetFullPath(Path.Combine(exeDir, "..", "model"));
        seedModel = Model.Load(Path.Combine(seedDir, "model.txt")) ?? Model.Prior();
        eng.prior = seedModel.Clone();
        learner = new Learner(isReplay ? Path.Combine(Path.GetTempPath(), "palmguard-replay") : dataDir);
        if (isReplay)
        {
            eng.model = seedModel.Clone(); eng.learn = false; eng.calib.AssumeReference(); padDetected = true;
            return;
        }
        eng.model = Model.Load(Path.Combine(dataDir, "model.txt")) ?? seedModel.Clone();
        eng.calib.Load(Path.Combine(dataDir, "calib.txt"));
        eng.learn = settings.Learning;
        learner.Load(); learner.seed = Learner.LoadFile(Path.Combine(seedDir, "seed.csv"));
        eng.OnLabel = s => learner.Add(s);
        eng.OnDecision = (c, why) => Log("class", "id" + c.id + " " + c.cls + " " + why);
        StartSession();
        Log("start", "v" + VERSION + " model v" + eng.model.version + " seed=" + learner.seed.Count + " live=" + learner.live.Count + " calibrated=" + eng.calib.calibrated);
        InitGestures();
        StartMomentumThread();
    }

    static void StartSession()
    {
        try
        {
            var root = Path.Combine(dataDir, "sessions"); Directory.CreateDirectory(root);
            // retention: keep at most MaxSessions and MaxSessionMB
            var dirs = new DirectoryInfo(root).GetDirectories().OrderBy(d => d.Name).ToList();
            long total = dirs.Sum(d => d.GetFiles().Sum(f => f.Length));
            while (dirs.Count > 0 && (dirs.Count >= settings.MaxSessions || total > settings.MaxSessionMB * 1024L * 1024L))
            {
                total -= dirs[0].GetFiles().Sum(f => f.Length);
                try { dirs[0].Delete(true); } catch { }
                dirs.RemoveAt(0);
            }
            sessionDir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(sessionDir);
            log = new StreamWriter(Path.Combine(sessionDir, "events.csv")) { AutoFlush = true }; log.WriteLine("t,event,detail");
            if (settings.RecordSessions)
            {
                sessTouch = new StreamWriter(Path.Combine(sessionDir, "touch.csv")); sessTouch.WriteLine("t,phase,scan,contact_count,slot,id,tip,confidence,x,y,width,height,pressure,button,cur_x,cur_y");
                sessKeys = new StreamWriter(Path.Combine(sessionDir, "keys.csv")); sessKeys.WriteLine("t,phase,vk");
            }
        }
        catch (Exception e) { Error("session", e); }
    }

    static int shutdownOnce;
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdownOnce, 1) == 1) return;
        try { ReleaseInjected(); } catch { }
        try { if (rightFromTwo) { SendMouse(0x0010); rightFromTwo = false; } } catch { }
        try { if (hook != IntPtr.Zero) W32.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } catch { }
        RestoreGestures();
        if (replay) return;
        lock (Sync) { eng.calib.Save(Path.Combine(dataDir, "calib.txt")); }
        try { sessTouch.Flush(); sessTouch.Close(); sessKeys.Close(); } catch { }
        Log("stop", ""); try { log.Close(); } catch { }
    }

    // ---------------------------------------------------------------- gestures (SPI, non-persistent)
    static volatile bool wantGesturesOff; static bool gesturesOff; static AutoResetEvent spiEvt = new AutoResetEvent(false);
    static bool origPan = true, origZoom = true, scrollRev; public static double lastSpiMs; static bool spiStarted; static volatile bool spiDirty;
    static bool OwnScroll { get { return settings.Enabled && settings.MacScroll && padDetected; } }
    static bool PanWanted { get { return origPan && !OwnScroll; } }
    public static bool GesturesHeld { get { return wantGesturesOff; } }
    static byte[] GetTP() { var b = new byte[48]; BitConverter.GetBytes(1u).CopyTo(b, 0); return W32.SystemParametersInfo(0xAE, 48, b, 0) ? b : null; }
    static void SetPanZoom(bool pan, bool zoom)
    {
        var b = GetTP(); if (b == null) return;
        uint f2 = BitConverter.ToUInt32(b, 16);
        f2 = pan ? f2 | (1u << 7) : f2 & ~(1u << 7); f2 = zoom ? f2 | (1u << 8) : f2 & ~(1u << 8);
        BitConverter.GetBytes(f2).CopyTo(b, 16); W32.SystemParametersInfo(0xAF, 48, b, 0);
    }
    static void InitGestures()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad"))
                if (k != null) { origPan = Convert.ToInt32(k.GetValue("PanEnabled", 1)) != 0; origZoom = Convert.ToInt32(k.GetValue("ZoomEnabled", 1)) != 0; }
        }
        catch { }
        try { var b = GetTP(); if (b != null) scrollRev = (BitConverter.ToUInt32(b, 16) & (1u << 9)) != 0; } catch { }
        SetPanZoom(origPan, origZoom); // repair state left by a crash
        spiStarted = true;
        new Thread(() =>
        {
            while (true)
            {
                spiEvt.WaitOne();
                try
                {
                    while (wantGesturesOff != gesturesOff || spiDirty)
                    {
                        bool off = wantGesturesOff; spiDirty = false; var t0 = Stopwatch.StartNew();
                        if (off) SetPanZoom(false, false); else SetPanZoom(PanWanted, origZoom);
                        gesturesOff = off; lastSpiMs = t0.Elapsed.TotalMilliseconds; if (off) gestureOffCount++;
                    }
                }
                catch (Exception e) { Error("spi", e); }
            }
        }) { IsBackground = true, Name = "PalmGuard.SPI" }.Start();
        ApplyScrollSettings();
    }
    public static void RestoreGestures() { wantGesturesOff = false; if (spiStarted) { try { SetPanZoom(origPan, origZoom); } catch { } gesturesOff = false; } }
    static Gestures NewGestures() { var g = new Gestures(); ConfigureGestures(g); return g; }
    static void ConfigureGestures(Gestures g)
    {
        g.Natural = !scrollRev ^ settings.ScrollReverse; // follow Windows' scroll-direction setting
        g.Momentum = settings.Momentum; g.GainPerMm = 14 * settings.ScrollSpeed / 100.0;
    }
    // call after changing MacScroll/Enabled/ScrollReverse/Momentum/ScrollSpeed
    public static void ApplyScrollSettings()
    {
        lock (Sync) { ConfigureGestures(gest); if (!OwnScroll) gest.StopMomentum(); }
        spiDirty = true; if (spiStarted) spiEvt.Set();
        Log("scroll-settings", "own=" + OwnScroll + " natural=" + gest.Natural + " speed=" + settings.ScrollSpeed);
    }
    static void UpdateGestureWish(double t)
    {
        bool off = settings.Enabled && settings.GestureHold && t < gestureUntil;
        if (off != wantGesturesOff) { wantGesturesOff = off; if (spiStarted) spiEvt.Set(); }
    }

    // ---------------------------------------------------------------- raw input
    public static void RegisterInput(IntPtr hwnd)
    {
        var devs = new[] {
            new W32.RAWINPUTDEVICE { Page = 0x0D, Usage = 0x05, Flags = 0x100 | 0x2000, Target = hwnd },   // touchpad, INPUTSINK|DEVNOTIFY
            new W32.RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = 0x100, Target = hwnd } };           // keyboard
        if (!W32.RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf(typeof(W32.RAWINPUTDEVICE)))) Log("error", "RegisterRawInputDevices " + Marshal.GetLastWin32Error());
        // find the touchpad now rather than at the first touch (scroll takeover needs it before the first gesture)
        try
        {
            uint n = 0, sz = (uint)Marshal.SizeOf(typeof(W32.RAWINPUTDEVICELIST));
            W32.GetRawInputDeviceList(null, ref n, sz);
            var list = new W32.RAWINPUTDEVICELIST[n];
            if (n > 0 && W32.GetRawInputDeviceList(list, ref n, sz) != uint.MaxValue)
                lock (Sync)
                {
                    enumerating = true;
                    foreach (var d in list) if (d.dwType == 2 && padPP == IntPtr.Zero && IsPtpCollection(d.hDevice)) CheckPad(d.hDevice);
                    enumerating = false;
                }
        }
        catch (Exception e) { Error("enumerate", e); }
    }

    // enumerating: picked at startup without input (tentative; a real touch from another PTP replaces it).
    // Virtual touchpads (remote-desktop/streaming drivers, "VHF") are never picked by enumeration.
    static bool enumerating, padTentative;
    static bool IsPtpCollection(IntPtr dev)
    {   // RID_DEVICE_INFO: only the Digitizer/Touch Pad (0x0D/0x05) collection, not touchscreens or pens
        var buf = Marshal.AllocHGlobal(64);
        try
        {
            Marshal.WriteInt32(buf, 32); uint sz = 32;
            if (W32.GetRawInputDeviceInfo(dev, 0x2000000b, buf, ref sz) == uint.MaxValue) return false;
            return (ushort)Marshal.ReadInt16(buf, 20) == 0x0D && (ushort)Marshal.ReadInt16(buf, 22) == 0x05;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    static bool CheckPad(IntPtr dev)
    {
        bool r; if (isPad.TryGetValue(dev, out r)) { if (r && dev == padHandle && !enumerating) padTentative = false; return r; }
        if (padTentative && !enumerating && dev != padHandle)
        {   // input arrived from a different device than the one guessed at startup: re-evaluate from scratch
            Marshal.FreeHGlobal(padPP); padPP = IntPtr.Zero; padHandle = IntPtr.Zero; isPad.Clear(); padTentative = false;
        }
        uint sz = 0; W32.GetRawInputDeviceInfo(dev, 0x20000007, IntPtr.Zero, ref sz);
        IntPtr nb = Marshal.AllocHGlobal((int)sz * 2 + 2); W32.GetRawInputDeviceInfo(dev, 0x20000007, nb, ref sz);
        string name = Marshal.PtrToStringUni(nb) ?? ""; Marshal.FreeHGlobal(nb);
        sz = 0; W32.GetRawInputDeviceInfo(dev, 0x20000005, IntPtr.Zero, ref sz);
        if (sz == 0) { isPad[dev] = false; return false; }
        IntPtr pp = Marshal.AllocHGlobal((int)sz); W32.GetRawInputDeviceInfo(dev, 0x20000005, pp, ref sz);
        ushort n = 256; var vc = new byte[72 * n]; var lk = new List<ushort>();
        int maxX = 0, maxY = 0; double physX = 0, physY = 0;
        if (W32.HidP_GetValueCaps(0, vc, ref n, pp) == W32.HIDP_OK)
            for (int i = 0; i < n; i++)
            {
                int o = i * 72; ushort page = BitConverter.ToUInt16(vc, o), link = BitConverter.ToUInt16(vc, o + 6), usage = BitConverter.ToUInt16(vc, o + 56);
                uint units = BitConverter.ToUInt32(vc, o + 36); int exp = (int)BitConverter.ToUInt32(vc, o + 32); if (exp > 7) exp -= 16;
                int lmax = BitConverter.ToInt32(vc, o + 44), pmax = BitConverter.ToInt32(vc, o + 52);
                if (page == 0x0D && usage == 0x51 && !lk.Contains(link)) lk.Add(link);
                if (page == 0x01 && (usage == 0x30 || usage == 0x31) && link > 0 && lmax > 0)
                {
                    double mm = pmax > 0 ? pmax * Math.Pow(10, exp) * ((units & 0xF) == 0x3 ? 25.4 : 10) : 0; // English (inch) or SI (cm) linear
                    if (usage == 0x30) { maxX = lmax; physX = mm; } else { maxY = lmax; physY = mm; }
                }
            }
        r = lk.Count > 0 && maxX > 0 && maxY > 0 && padPP == IntPtr.Zero && !(enumerating && name.IndexOf("VHF", StringComparison.OrdinalIgnoreCase) >= 0);
        if (r)
        {
            padPP = pp; padHandle = dev; links = lk; padName = name; padDetected = true; padTentative = enumerating;
            eng.geom.maxX = maxX; eng.geom.maxY = maxY;
            if (physX > 20 && physY > 20 && physX < 400 && physY < 400) { eng.geom.mmX = physX / maxX; eng.geom.mmY = physY / maxY; }
            else { eng.geom.mmX = eng.geom.mmY = 105.0 / maxX; } // no usable physical size: assume ~105 mm wide
            if (!eng.calib.calibrated && name.IndexOf("BLTP784", StringComparison.OrdinalIgnoreCase) >= 0) eng.calib.AssumeReference();
            spiDirty = true; if (spiStarted) spiEvt.Set();
            Log("device", name + " " + maxX + "x" + maxY + " " + eng.geom.Wmm.ToString("F1") + "x" + eng.geom.Hmm.ToString("F1") + "mm slots=" + lk.Count + " calibrated=" + eng.calib.calibrated);
        }
        else Marshal.FreeHGlobal(pp);
        isPad[dev] = r || dev == padHandle; return isPad[dev];
    }

    public static void OnDeviceChange(IntPtr wParam, IntPtr dev)
    {
        isPad.Remove(dev);
        if (wParam.ToInt32() == 2 && dev == padHandle) { ResetDevice("device removed"); }
    }
    public static void ResetDevice(string why)
    {
        lock (Sync)
        {
            padHandle = IntPtr.Zero; padPP = IntPtr.Zero; links = new List<ushort>(); isPad.Clear(); padDetected = false;
            eng.Active.Clear(); blocking = false; blockUntil = -1; gestureUntil = -1; rawButton = false; pendingPress = false;
            ReleaseInjected();
            if (rightFromTwo) { SendMouse(0x0010); rightFromTwo = false; }
            gest.StopMomentum(); gest = NewGestures();
            lDownSwallowed = rDownSwallowed = swallowTapUp = false;
        }
        spiDirty = true; if (spiStarted) spiEvt.Set(); // pan back on until the pad is found again
        Log("reset-device", why);
    }

    static uint Val(byte[] rpt, ushort page, ushort link, ushort usage)
    { uint v; return W32.HidP_GetUsageValue(0, page, link, usage, out v, padPP, rpt, (uint)rpt.Length) == W32.HIDP_OK ? v : 0; }
    static bool IsModifier(ushort vk) { return vk == 16 || vk == 17 || vk == 18 || vk == 91 || vk == 92 || (vk >= 160 && vk <= 165); }

    public static void OnRawInput(IntPtr lp)
    {
        uint sz = 0, hdr = (uint)(8 + IntPtr.Size * 2);
        W32.GetRawInputData(lp, 0x10000003, IntPtr.Zero, ref sz, hdr);
        if (sz == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)sz);
        try
        {
            W32.GetRawInputData(lp, 0x10000003, buf, ref sz, hdr);
            uint type = (uint)Marshal.ReadInt32(buf); IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            double t = Now();
            lock (Sync)
            {
                if (type == 1)
                {
                    ushort flags = (ushort)Marshal.ReadInt16(buf, (int)hdr + 2), vk = (ushort)Marshal.ReadInt16(buf, (int)hdr + 6);
                    if ((flags & 1) == 0 && !IsModifier(vk)) { eng.OnKey(t); sessKeys.WriteLine("{0:F4},live,0", t); gest.StopMomentum(); }
                    return;
                }
                if (type != 2 || !CheckPad(dev) || padPP == IntPtr.Zero) return;
                int sizeHid = Marshal.ReadInt32(buf, (int)hdr), count = Marshal.ReadInt32(buf, (int)hdr + 4);
                for (int r = 0; r < count; r++)
                {
                    var rpt = new byte[sizeHid]; Marshal.Copy(buf + (int)hdr + 8 + r * sizeHid, rpt, 0, sizeHid);
                    ProcessReport(rpt, t);
                }
            }
        }
        catch (Exception e) { Error("rawinput", e); blocking = false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void ProcessReport(byte[] rpt, double t)
    {
        uint ccount = Val(rpt, 0x0D, 0, 0x54), scan = Val(rpt, 0x0D, 0, 0x56);
        var frame = new List<Touch>(); int slot = 0;
        var bu = new ushort[8]; uint bul = 8;
        bool button = W32.HidP_GetUsages(0, 0x09, 0, bu, ref bul, padPP, rpt, (uint)rpt.Length) == W32.HIDP_OK && bul > 0;
        foreach (ushort link in links)
        {
            if (slot++ >= ccount) break;
            var us = new ushort[16]; uint ul = 16; bool tip = false, conf = false;
            if (W32.HidP_GetUsages(0, 0x0D, link, us, ref ul, padPP, rpt, (uint)rpt.Length) == W32.HIDP_OK)
                for (int i = 0; i < ul; i++) { if (us[i] == 0x42) tip = true; if (us[i] == 0x47) conf = true; }
            var tc = new Touch { id = Val(rpt, 0x0D, link, 0x51), tip = tip, conf = conf, x = (int)Val(rpt, 0x01, link, 0x30), y = (int)Val(rpt, 0x01, link, 0x31), w = (int)Val(rpt, 0x0D, link, 0x48), h = (int)Val(rpt, 0x0D, link, 0x49) };
            frame.Add(tc);
            sessTouch.WriteLine("{0:F4},live,{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},0,{11},0,0", t, scan, ccount, link, tc.id, tip ? 1 : 0, conf ? 1 : 0, tc.x, tc.y, tc.w, tc.h, button ? 1 : 0);
        }
        Feed(t, frame, button);
    }

    // shared by live input and replay; caller holds Sync
    public static void Feed(double t, List<Touch> frame, bool button)
    {
        bool pressEdge = button && !rawButton, releaseEdge = !button && rawButton;
        if (button != rawButton) { rawButton = button; Log("pad-button", button ? "down" : "up"); }

        var before = new Dictionary<uint, Sample>(); foreach (var c in eng.Active.Values) if (c.cls == Cls.Finger) before[c.id] = c.Last;
        eng.OnFrame(t, frame, button);
        foreach (var c in eng.Active.Values)
        {
            Sample b; if (before.TryGetValue(c.id, out b) && c.cls == Cls.Finger) { var L = c.Last; if (Math.Abs(L.x - b.x) + Math.Abs(L.y - b.y) > 0.3) motionSinceHook++; }
            Splat(c);
        }
        if (!replay)
        {
            // thumb-zone taps: remember so Windows' tap-click / tap-and-drag can be ignored
            for (int k = eng.Recent.Count - 1; k >= 0 && eng.Recent[k].tEnd == t; k--)
            {
                var rc = eng.Recent[k];
                if (rc.tEnd - rc.t0 < THUMB_TAP_MAX && rc.First.y >= eng.cfg.ThumbZone * eng.geom.Hmm) lastThumbTapT = t;
            }
            // click recovery: physical press that Windows didn't turn into a button-down
            if (settings.Enabled && settings.ClickRecovery && pressEdge) { padDownT = t; pendingPress = !blocking && t - lastPassedDownT > 0.03; }
            if (pendingPress && t - padDownT >= RECOVER_AFTER)
            {
                pendingPress = false;
                if (lastPassedDownT < padDownT - 0.03)
                {
                    bool right = settings.TwoFingerClick && gest.IsTwoFingerPress(eng);
                    SendMouse(right ? 0x0008u : 0x0002u); injectedUp = right ? 0x0010u : 0x0004u; clicksRecovered++; Log("click-recovered", right ? "right down" : "down");
                }
            }
            if (releaseEdge) { pendingPress = false; if (injectedDown) { ReleaseInjected(); Log("click-recovered", "up"); } }
            if (pressEdge) gest.StopMomentum();
            // Mac-style scrolling: two fingers -> wheel events with axis lock (Windows pan is switched off)
            if (OwnScroll)
            {
                bool wasMomentum = gest.momentumActive;
                int wy, wx; gest.Frame(t, eng, out wy, out wx);
                SendWheel(wy, wx);
                if (gest.mode == Gestures.Mode.Scroll) gestureUntil = Math.Max(gestureUntil, t + GESTURE_HOLD); // no accidental zoom mid-scroll
                if (gest.momentumActive && !wasMomentum) momentumEvt.Set();
            }
        }
        string reason; bool want = eng.ShouldBlock(out reason);
        if (want) { blockUntil = t + HOLD_AFTER; gestureUntil = t + GESTURE_HOLD; lastReason = reason; }
        bool nb = settings.Enabled && (want || t < blockUntil);
        if (nb != blocking) { blocking = nb; Log(nb ? "block-on" : "block-off", reason); }
        UpdateGestureWish(t);
    }

    static void Splat(Contact c)
    {
        var L = c.Last;
        float gx = (float)(L.x / eng.geom.Wmm * (GW - 1)), gy = (float)(L.y / eng.geom.Hmm * (GH - 1));
        float ux = (float)(eng.geom.Wmm / 32.0 / (eng.geom.Wmm / GW)); // ~one sensor cell per size unit on the reference pad
        float rx = Math.Max(1.2f, L.w * 0.9f * ux / 2.9f), ry = Math.Max(1.2f, L.h * 0.9f * ux / 2.9f);
        var grid = c.cls == Cls.Finger ? heatFinger : heatPalm;
        int x0 = (int)Math.Max(0, gx - rx * 2), x1 = (int)Math.Min(GW - 1, gx + rx * 2), y0 = (int)Math.Max(0, gy - ry * 2), y1 = (int)Math.Min(GH - 1, gy + ry * 2);
        for (int i = x0; i <= x1; i++) for (int j = y0; j <= y1; j++)
            { float dx = (i - gx) / rx, dy = (j - gy) / ry, d2 = dx * dx + dy * dy; if (d2 < 4) grid[i, j] += 0.02f * (float)Math.Exp(-d2 * 1.5); }
    }

    // ---------------------------------------------------------------- periodic (input thread, ~30 Hz)
    static int tickN;
    public static void CoreTick()
    {
        try
        {
            double t = Now();
            lock (Sync)
            {
                string r; if (blocking && t >= blockUntil && !eng.ShouldBlock(out r)) { blocking = false; Log("block-off", "timeout"); }
                if (!settings.Enabled) blocking = false;
                UpdateGestureWish(t);
                for (int i = 0; i < GW; i++) for (int j = 0; j < GH; j++) { heatPalm[i, j] *= 0.9985f; heatFinger[i, j] *= 0.9985f; }
                tlPos = (tlPos + 1) % TL;
                tlState[tlPos] = (byte)(!settings.Enabled ? 0 : blocking ? 3 : wantGesturesOff ? 2 : eng.Active.Count > 0 ? 1 : 0);
                tlKey[tlPos] = eng.lastKey > lastKeyDrawn; lastKeyDrawn = eng.lastKey;
            }
            if (replay) return;
            // hook watchdog: Windows silently removes LL hooks that time out. If plenty of finger motion arrives
            // with no hook callbacks, send a zero-distance probe event; reinstall only if the hook doesn't see it.
            if (!probing && motionSinceHook > 80 && t - lastProbeT > 10)
            {
                probing = true; probeOk = false; probeT = lastProbeT = t;
                var a = new[] { new W32.INPUT { type = 0, mi = new W32.MOUSEINPUT { dwFlags = 0x0001, dwExtraInfo = PROBE_TAG } } };
                W32.SendInput(1, a, Marshal.SizeOf(typeof(W32.INPUT)));
            }
            if (probing && t - probeT > 0.3)
            {
                probing = false; motionSinceHook = 0;
                if (!probeOk) { InstallHook(); hookReinstalls++; Log("hook-reinstalled", "probe not seen"); }
            }
            if (++tickN % 60 == 0) { try { sessTouch.Flush(); sessKeys.Flush(); } catch { } }
            MaybeTrain(false);
        }
        catch (Exception e) { Error("tick", e); }
    }

    // ---------------------------------------------------------------- learning
    public static void MaybeTrain(bool force)
    {
        if (!settings.Learning || learner.training || (!force && learner.sinceTrain < 40)) return;
        learner.training = true;
        var cur = eng.model;
        Task.Run(() =>
        {
            try
            {
                var m = learner.TrainAndGate(cur, eng.prior, s => Log("train", s));
                if (m != null) { eng.model = m; m.Save(Path.Combine(dataDir, "model.txt")); }
                lock (Sync) eng.calib.Save(Path.Combine(dataDir, "calib.txt"));
            }
            catch (Exception e) { Error("train", e); }
            finally { learner.training = false; }
        });
    }
    public static void SetLearning(bool on) { settings.Learning = on; eng.learn = on; settings.Save(); }

    public static void CorrectMissed()
    {
        int n; lock (Sync) n = eng.MarkMissedPalm(Now());
        Log("user-missed-palm", n.ToString());
        Notify(n > 0 ? "Learned: " + n + " recent touch(es) were palms" : "No recent finger touches to correct");
        if (n > 0) MaybeTrain(true);
    }
    public static void CorrectWrong()
    {
        int n; lock (Sync) n = eng.MarkFalsePalm(Now());
        Log("user-false-palm", n.ToString());
        Notify(n > 0 ? "Learned: " + n + " recent touch(es) were fingers" : "No recently blocked touches to correct");
        if (n > 0) MaybeTrain(true);
    }
    public static void ResetLearning()
    {
        lock (Sync)
        {
            foreach (var f in new[] { "labels.csv", "model.txt", "calib.txt" }) { try { File.Delete(Path.Combine(dataDir, f)); } catch { } }
            learner.live.Clear(); eng.model = seedModel.Clone(); eng.calib = new Calib();
            if (padName.IndexOf("BLTP784", StringComparison.OrdinalIgnoreCase) >= 0) eng.calib.AssumeReference();
        }
        Log("reset", ""); Notify("Learning reset to the seed model");
    }
    public static void SetEnabled(bool on)
    {
        settings.Enabled = on; settings.Save();
        if (!on) { blocking = false; UpdateGestureWish(Now()); }
        ApplyScrollSettings();
        Log(on ? "enabled" : "disabled", ""); Notify(on ? "PalmGuard resumed" : "PalmGuard paused - stock touchpad behaviour");
    }

    // ---------------------------------------------------------------- mouse hook
    public static void InstallHook()
    {
        if (hook != IntPtr.Zero) { W32.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        hookRef = HookCb; hook = W32.SetWindowsHookEx(14, hookRef, W32.GetModuleHandle(null), 0);
        Interlocked.Exchange(ref lastHookTicks, Stopwatch.GetTimestamp());
        if (hook == IntPtr.Zero) Log("error", "SetWindowsHookEx " + Marshal.GetLastWin32Error());
    }
    static void ReleaseInjected() { if (injectedUp != 0) { SendMouse(injectedUp); injectedUp = 0; } }
    static void SendWheel(int wy, int wx)
    {
        if (wy == 0 && wx == 0) return;
        var a = new List<W32.INPUT>();
        if (wy != 0) a.Add(new W32.INPUT { type = 0, mi = new W32.MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)wy), dwExtraInfo = INJECT_TAG } });
        if (wx != 0) a.Add(new W32.INPUT { type = 0, mi = new W32.MOUSEINPUT { dwFlags = 0x01000, mouseData = unchecked((uint)wx), dwExtraInfo = INJECT_TAG } });
        W32.SendInput((uint)a.Count, a.ToArray(), Marshal.SizeOf(typeof(W32.INPUT)));
    }
    static AutoResetEvent momentumEvt = new AutoResetEvent(false);
    public static void StartMomentumThread()
    {
        new Thread(() =>
        {
            while (true)
            {
                momentumEvt.WaitOne();
                W32.timeBeginPeriod(1);
                try
                {
                    while (true)
                    {
                        int wy, wx; bool more;
                        lock (Sync) { gest.Tick(Now(), out wy, out wx); more = gest.momentumActive && OwnScroll; if (!more) gest.StopMomentum(); }
                        SendWheel(wy, wx);
                        if (!more) break;
                        Thread.Sleep(8);
                    }
                }
                catch (Exception e) { Error("momentum", e); }
                finally { W32.timeEndPeriod(1); }
            }
        }) { IsBackground = true, Name = "PalmGuard.Momentum", Priority = ThreadPriority.AboveNormal }.Start();
    }
    static bool TwoFingerNow() { lock (Sync) return settings.TwoFingerClick && gest.IsTwoFingerPress(eng); }
    static void SendMouse(uint flags)
    {
        var a = new[] { new W32.INPUT { type = 0, mi = new W32.MOUSEINPUT { dwFlags = flags, dwExtraInfo = INJECT_TAG } } };
        W32.SendInput(1, a, Marshal.SizeOf(typeof(W32.INPUT)));
    }
    static bool ClickReleases()
    {
        lock (Sync)
        {
            if (!eng.OnClick(Now())) return false;
            blocking = false; blockUntil = -1;
        }
        Log("click-release", "thumb"); return true;
    }
    static IntPtr HookCb(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            Interlocked.Exchange(ref lastHookTicks, Stopwatch.GetTimestamp());
            motionSinceHook = 0;
            if (code >= 0 && wParam.ToInt32() == 0x0200)
            {
                var pi = (W32.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(W32.MSLLHOOKSTRUCT));
                if (pi.extra == PROBE_TAG) { probeOk = true; return W32.CallNextHookEx(hook, code, wParam, lParam); }
            }
            if (code >= 0 && settings.Enabled)
            {
                var info = (W32.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(W32.MSLLHOOKSTRUCT));
                bool injected = (info.flags & 1) != 0; int msg = wParam.ToInt32(); bool swallow = false;
                switch (msg)
                {
                    case 0x0200: swallow = !injected && (blocking || (settings.MacScroll && Now() < gest.scrollUntil)); break; // no cursor drift while scrolling
                    case 0x020A: case 0x020E: swallow = blocking && info.extra != INJECT_TAG; break;
                    case 0x0201:
                        if (!injected && settings.ThumbTapIgnore && !rawButton && Now() - lastThumbTapT < TAP_CLICK_WINDOW)
                        { swallow = true; swallowTapUp = true; thumbTapsIgnored++; Log("thumb-tap-ignored", ""); break; }
                        if (!injected) gest.StopMomentum();
                        if (!injected && TwoFingerNow())
                        {   // two fingers down + physical press = right click (Mac-style), even mid-scroll
                            swallow = true; rightFromTwo = true; SendMouse(0x0008); twoFingerClicks++; gest.rightClicks++;
                            lastPassedDownT = Now(); pendingPress = false; Log("two-finger-click", ""); break;
                        }
                        swallow = blocking && !injected && !ClickReleases(); lDownSwallowed = swallow;
                        if (!swallow && !injected) { lastPassedDownT = Now(); pendingPress = false; }
                        break;
                    case 0x0204:
                        swallow = blocking && !injected && !ClickReleases(); rDownSwallowed = swallow;
                        if (!swallow && !injected) { lastPassedDownT = Now(); pendingPress = false; }
                        break;
                    case 0x0202:
                        if (!injected && rightFromTwo) { swallow = true; rightFromTwo = false; SendMouse(0x0010); break; }
                        if (!injected && swallowTapUp) { swallow = true; swallowTapUp = false; break; }
                        swallow = lDownSwallowed && !injected; if (!injected) lDownSwallowed = false; break;
                    case 0x0205: swallow = rDownSwallowed && !injected; if (!injected) rDownSwallowed = false; break;
                }
                if (swallow) { blockedEvents++; return (IntPtr)1; }
            }
        }
        catch (Exception e) { Error("hook", e); } // fail open
        return W32.CallNextHookEx(hook, code, wParam, lParam);
    }

    // ---------------------------------------------------------------- dashboard snapshot
    public class CView { public float x, y; public int w, h; public Cls cls; public bool conf; public double p, age; public string why; public PointF[] trail; public double[] feat; }
    public class Snap
    {
        public List<CView> contacts = new List<CView>();
        public bool blocking, gesturesHeld, enabled, training, calibrated, padDetected;
        public string reason; public int modelVersion, labels, strong, accepted, rejected, palms, fwPalms, released;
        public double fingerW, fingerH, wmm, hmm, edgeMm; public double[] weights;
    }
    public static Snap TakeSnapshot()
    {
        var s = new Snap();
        lock (Sync)
        {
            double now = Now();
            foreach (var c in eng.Active.Values)
            {
                if (c.hist.Count == 0) continue;
                int from = Math.Max(0, c.hist.Count - 60); var tr = new PointF[c.hist.Count - from];
                for (int k = from; k < c.hist.Count; k++) tr[k - from] = new PointF((float)c.hist[k].x, (float)c.hist[k].y);
                var L = c.Last;
                s.contacts.Add(new CView { x = (float)L.x, y = (float)L.y, w = L.w, h = L.h, cls = c.cls, conf = c.conf, p = c.p, age = now - c.t0, why = c.why, trail = tr, feat = c.lastFeat == null ? null : (double[])c.lastFeat.Clone() });
            }
            s.blocking = blocking; s.gesturesHeld = wantGesturesOff; s.enabled = settings.Enabled; s.reason = lastReason;
            s.modelVersion = eng.model.version; s.weights = (double[])eng.model.w.Clone();
            s.labels = learner.live.Count; s.strong = learner.StrongCount; s.accepted = learner.accepted; s.rejected = learner.rejected; s.training = learner.training;
            s.palms = eng.palmsModel + eng.palmsHard; s.fwPalms = eng.palmsFw; s.released = eng.released + eng.thumbClicks;
            s.fingerW = eng.calib.fingerW; s.fingerH = eng.calib.fingerH; s.calibrated = eng.calib.calibrated;
            s.wmm = eng.geom.Wmm; s.hmm = eng.geom.Hmm; s.edgeMm = eng.cfg.EdgeScaleMm; s.padDetected = padDetected;
        }
        return s;
    }
}

// ======================================================================================
// Input thread: message-only window receiving raw input, hotkeys, device changes
// ======================================================================================
class InputWindow : NativeWindow
{
    public const int WM_RESUME = W32.WM_APP + 2;
    public static InputWindow Instance;
    public Action<int> OnHotkey;

    public static void Start(Action<int> onHotkey)
    {
        var ready = new ManualResetEvent(false);
        var th = new Thread(() =>
        {
            Instance = new InputWindow { OnHotkey = onHotkey };
            Instance.CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // HWND_MESSAGE
            Core.RegisterInput(Instance.Handle);
            W32.RegisterHotKey(Instance.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x50); // Ctrl+Alt+P
            W32.RegisterHotKey(Instance.Handle, 2, 0x0002 | 0x0001 | 0x4000, 0x5A); // Ctrl+Alt+Z
            W32.RegisterHotKey(Instance.Handle, 3, 0x0002 | 0x0001 | 0x4000, 0x58); // Ctrl+Alt+X
            Core.InstallHook();
            var tick = new System.Windows.Forms.Timer { Interval = 33 }; tick.Tick += (s, e) => Core.CoreTick(); tick.Start();
            ready.Set();
            Application.Run();
        }) { Name = "PalmGuard.Input", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        th.SetApartmentState(ApartmentState.STA); th.Start();
        ready.WaitOne(5000);
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            switch (m.Msg)
            {
                case W32.WM_INPUT: Core.OnRawInput(m.LParam); break;
                case W32.WM_INPUT_DEVICE_CHANGE: Core.OnDeviceChange(m.WParam, m.LParam); break;
                case W32.WM_HOTKEY: if (OnHotkey != null) OnHotkey(m.WParam.ToInt32()); break;
                case WM_RESUME: Core.ResetDevice("resume"); Core.RegisterInput(Handle); Core.InstallHook(); break;
            }
        }
        catch (Exception e) { Core.Error("inputwnd", e); }
        base.WndProc(ref m);
    }
}

// ======================================================================================
// Dashboard
// ======================================================================================
class Dashboard : Form
{
    static readonly Color BG = Color.FromArgb(14, 17, 23), PANEL = Color.FromArgb(22, 27, 36), PAD = Color.FromArgb(30, 36, 48),
        GRID = Color.FromArgb(40, 48, 62), TXT = Color.FromArgb(226, 232, 240), DIM = Color.FromArgb(128, 140, 160),
        CYAN = Color.FromArgb(56, 214, 255), RED = Color.FromArgb(255, 84, 84), AMBER = Color.FromArgb(255, 184, 64),
        GREEN = Color.FromArgb(64, 220, 140), GREYC = Color.FromArgb(120, 128, 140), VIOLET = Color.FromArgb(170, 140, 255);

    class Canvas : Control { public Canvas() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = BG; } }

    Canvas cv; Button pause; System.Windows.Forms.Timer timer; Bitmap heatBmp = new Bitmap(Core.GW, Core.GH, PixelFormat.Format32bppArgb);
    Font fTitle = new Font("Segoe UI Semibold", 15f), fBig = new Font("Segoe UI Semibold", 17f), fMed = new Font("Segoe UI", 9.5f),
         fSmall = new Font("Segoe UI", 8.5f), fPill = new Font("Segoe UI Semibold", 10f);
    string toast = ""; double toastUntil = -1;
    public bool replayMode;

    public Dashboard()
    {
        Text = "PalmGuard"; BackColor = BG; ForeColor = TXT; ClientSize = new Size(1040, 640);
        MinimumSize = new Size(900, 560); StartPosition = FormStartPosition.CenterScreen;
        try { Icon = AppIcon.Get(false); } catch { }
        cv = new Canvas { Dock = DockStyle.Fill }; cv.Paint += (s, e) => { try { Draw(e.Graphics, cv.ClientRectangle, Core.TakeSnapshot()); } catch (Exception ex) { Core.Error("draw", ex); } };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = BG, Padding = new Padding(14, 6, 14, 6), WrapContents = false };
        pause = MkBtn("Pause  (Ctrl+Alt+P)", 170); pause.Click += (s, e) => Core.SetEnabled(!Core.settings.Enabled);
        var missed = MkBtn("Palm got through  (Ctrl+Alt+Z)", 240); missed.Click += (s, e) => Core.CorrectMissed();
        var wrong = MkBtn("Finger was ignored  (Ctrl+Alt+X)", 250); wrong.Click += (s, e) => Core.CorrectWrong();
        var clear = MkBtn("Clear heat map", 120); clear.Click += (s, e) => { Array.Clear(Core.heatPalm, 0, Core.heatPalm.Length); Array.Clear(Core.heatFinger, 0, Core.heatFinger.Length); };
        var reset = MkBtn("Reset learning", 120); reset.Click += (s, e) =>
        {
            if (MessageBox.Show(this, "Discard everything PalmGuard learned on this PC and go back to the seed model?", "Reset learning", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Core.ResetLearning();
        };
        bar.Controls.AddRange(new Control[] { pause, missed, wrong, clear, reset });
        Controls.Add(cv); Controls.Add(bar);
        timer = new System.Windows.Forms.Timer { Interval = 33 };
        timer.Tick += (s, e) => { if (Visible && WindowState != FormWindowState.Minimized) { pause.Text = (Core.settings.Enabled ? "Pause" : "Resume") + "  (Ctrl+Alt+P)"; cv.Invalidate(); } };
        timer.Start();
    }
    Button MkBtn(string t, int w)
    {
        var b = new Button { Text = t, FlatStyle = FlatStyle.Flat, BackColor = PANEL, ForeColor = TXT, Font = fMed, Size = new Size(w, 32), Cursor = Cursors.Hand, TabStop = false, Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderColor = GRID; b.FlatAppearance.MouseOverBackColor = PAD; return b;
    }
    public void ShowToast(string s) { toast = s; toastUntil = Core.Now() + 4; }
    public void Shot(string path)
    {
        using (var b = new Bitmap(cv.Width, cv.Height)) { cv.DrawToBitmap(b, new Rectangle(0, 0, cv.Width, cv.Height)); b.Save(path, ImageFormat.Png); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!replayMode && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } // close = hide to tray
        base.OnFormClosing(e);
    }

    static Color ColOf(Core.CView c) { return c.cls == Cls.Finger ? CYAN : c.cls == Cls.Pending ? AMBER : c.conf ? RED : GREYC; }
    static string NameOf(Core.CView c) { return c.cls == Cls.Finger ? "finger" : c.cls == Cls.Pending ? "checking" : c.conf ? "PALM" : "palm (fw)"; }
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }

    void Draw(Graphics g, Rectangle rc, Core.Snap s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.DrawString("PalmGuard", fTitle, new SolidBrush(TXT), 18, 14);
        string st; Color sc;
        if (!s.enabled) { st = "PAUSED"; sc = GREYC; } else if (s.blocking) { st = "BLOCKING · " + s.reason; sc = RED; } else if (s.gesturesHeld) { st = "GESTURES HELD"; sc = AMBER; } else { st = "WATCHING"; sc = GREEN; }
        var ps = g.MeasureString(st, fPill); var pr = new RectangleF(140, 17, Math.Min(ps.Width + 26, rc.Width - 470), 26);
        using (var p = Round(pr, 13)) { g.FillPath(new SolidBrush(Color.FromArgb(40, sc)), p); g.DrawPath(new Pen(sc, 1.5f), p); }
        g.FillEllipse(new SolidBrush(sc), pr.X + 9, pr.Y + 9, 8, 8);
        g.DrawString(st, fPill, new SolidBrush(sc), new RectangleF(pr.X + 20, pr.Y + 4, pr.Width - 24, 20));
        g.DrawString("v" + Core.VERSION, fSmall, new SolidBrush(DIM), rc.Width - 60, 20);

        int side = 260, top = 62, bottomH = 100;
        var padArea = new Rectangle(18, top, rc.Width - side - 46, rc.Height - top - bottomH - 26);
        float aspect = (float)(s.wmm / s.hmm), pw = padArea.Width, ph = pw / aspect;
        if (ph > padArea.Height) { ph = padArea.Height; pw = ph * aspect; }
        var pad = new RectangleF(padArea.X + (padArea.Width - pw) / 2, padArea.Y + (padArea.Height - ph) / 2, pw, ph);
        DrawPad(g, pad, s);
        DrawSide(g, new RectangleF(rc.Width - side - 18, top, side, rc.Height - top - bottomH - 14), s);
        DrawTimeline(g, new RectangleF(18, rc.Height - bottomH, rc.Width - 36, bottomH - 10));
        if (Core.Now() < toastUntil)
        {
            var ts = g.MeasureString(toast, fMed); var tr = new RectangleF(pad.X + pad.Width / 2 - ts.Width / 2 - 14, pad.Y + 12, ts.Width + 28, 30);
            using (var p = Round(tr, 15)) g.FillPath(new SolidBrush(Color.FromArgb(230, VIOLET)), p);
            g.DrawString(toast, fMed, Brushes.Black, tr.X + 14, tr.Y + 6);
        }
    }

    void DrawPad(Graphics g, RectangleF pad, Core.Snap s)
    {
        using (var path = Round(pad, 18))
        {
            g.FillPath(new SolidBrush(PAD), path);
            var oldClip = g.Clip; g.SetClip(path);
            float edgePx = (float)(pad.Width * s.edgeMm / s.wmm);
            using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(34, AMBER), Color.Transparent))
            { g.FillRectangle(hb, pad.X, pad.Y, edgePx, pad.Height); g.FillRectangle(hb, pad.Right - edgePx, pad.Y, edgePx, pad.Height); }
            float tzY = pad.Y + pad.Height * (float)Core.eng.cfg.ThumbZone;
            using (var tp = new Pen(Color.FromArgb(60, CYAN), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(tp, pad.X, tzY, pad.Right, tzY);
            g.DrawString("thumb zone", fSmall, new SolidBrush(Color.FromArgb(110, CYAN)), pad.X + edgePx + 6, tzY + 3);
            using (var gp = new Pen(Color.FromArgb(26, 255, 255, 255)))
                for (int i = 1; i < 8; i++) { float x = pad.X + pad.Width * i / 8; g.DrawLine(gp, x, pad.Y, x, pad.Bottom); if (i < 5) { float y = pad.Y + pad.Height * i / 5; g.DrawLine(gp, pad.X, y, pad.Right, y); } }
            RenderHeat();
            var oldI = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(heatBmp, new RectangleF(pad.X - pad.Width / Core.GW / 2, pad.Y - pad.Height / Core.GH / 2, pad.Width + pad.Width / Core.GW, pad.Height + pad.Height / Core.GH));
            g.InterpolationMode = oldI;
            float pxPerMm = (float)(pad.Width / s.wmm);
            foreach (var c in s.contacts)
            {
                float cx = pad.X + c.x * pxPerMm, cy = pad.Y + c.y * pxPerMm; Color col = ColOf(c);
                if (c.trail.Length > 1) g.DrawLines(new Pen(Color.FromArgb(120, col), 2f), c.trail.Select(p => new PointF(pad.X + p.X * pxPerMm, pad.Y + p.Y * pxPerMm)).ToArray());
                float cell = (float)(s.wmm / 32.0) * pxPerMm;
                float rw = Math.Max(14, c.w * cell), rh = Math.Max(14, c.h * cell);
                for (int k = 3; k >= 1; k--) g.FillEllipse(new SolidBrush(Color.FromArgb(22 * k, col)), cx - rw / 2 - k * 5, cy - rh / 2 - k * 5, rw + k * 10, rh + k * 10);
                g.FillEllipse(new SolidBrush(Color.FromArgb(150, col)), cx - rw / 2, cy - rh / 2, rw, rh);
                g.DrawEllipse(new Pen(col, 2f), cx - rw / 2, cy - rh / 2, rw, rh);
                g.DrawArc(new Pen(Color.FromArgb(230, RED), 3f), cx - rw / 2 - 8, cy - rh / 2 - 8, rw + 16, rh + 16, -90, (float)(360 * c.p));
                string lab = NameOf(c) + "  p=" + c.p.ToString("F2") + "  W" + c.w + " H" + c.h, lab2 = c.why ?? "";
                var ls = g.MeasureString(lab, fSmall); var ls2 = g.MeasureString(lab2, fSmall); float lw = Math.Max(ls.Width, ls2.Width);
                float lx = Math.Min(Math.Max(cx - lw / 2, pad.X + 6), pad.Right - lw - 6), ly = cy + rh / 2 + 12;
                if (ly + 34 > pad.Bottom - 4) ly = cy - rh / 2 - 46;
                using (var lp = Round(new RectangleF(lx - 6, ly - 2, lw + 12, 34), 8)) g.FillPath(new SolidBrush(Color.FromArgb(210, BG)), lp);
                g.DrawString(lab, fSmall, new SolidBrush(col), lx, ly); g.DrawString(lab2, fSmall, new SolidBrush(DIM), lx, ly + 15);
            }
            g.Clip = oldClip;
            g.DrawPath(new Pen(s.blocking ? RED : GRID, s.blocking ? 2.5f : 1.5f), path);
        }
        g.DrawString(string.Format("{0:F0} × {1:F0} mm   ·   edge zones {2:F0} mm{3}", s.wmm, s.hmm, s.edgeMm, s.calibrated ? "" : "   ·   calibrating finger size…"), fSmall, new SolidBrush(DIM), pad.X, pad.Bottom + 4);
        if (!s.padDetected) g.DrawString("touch the pad once to detect it", fMed, new SolidBrush(DIM), pad.X + pad.Width / 2 - 100, pad.Y + pad.Height / 2 - 10);
    }

    void RenderHeat()
    {
        int GW = Core.GW, GH = Core.GH;
        var bd = heatBmp.LockBits(new Rectangle(0, 0, GW, GH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var px = new int[GW * GH];
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                float p = 1f - (float)Math.Exp(-Core.heatPalm[i, j] * 1.2), f = 1f - (float)Math.Exp(-Core.heatFinger[i, j] * 1.2);
                float a = Math.Min(1f, p + f * 0.8f); if (a < 0.01f) { px[j * GW + i] = 0; continue; }
                float wp = p / (p + f + 1e-6f);
                float r = wp * (180 + 75 * p) + (1 - wp) * (20 + 40 * f), gg = wp * (40 + 180 * p * p) + (1 - wp) * (140 + 80 * f), b = wp * (40 * (1 - p)) + (1 - wp) * (200 + 55 * f);
                px[j * GW + i] = ((int)(a * 210) << 24) | ((int)Math.Min(255, r) << 16) | ((int)Math.Min(255, gg) << 8) | (int)Math.Min(255, b);
            }
        Marshal.Copy(px, 0, bd.Scan0, px.Length); heatBmp.UnlockBits(bd);
    }

    void DrawSide(Graphics g, RectangleF r, Core.Snap s)
    {
        using (var p = Round(r, 14)) g.FillPath(new SolidBrush(PANEL), p);
        float y = r.Y + 12, x = r.X + 16, half = (r.Width - 32) / 2;
        Stat2(g, x, half, ref y, s.palms, "palms caught", RED, s.fwPalms, "by firmware", GREYC);
        Stat2(g, x, half, ref y, Core.blockedEvents, "events blocked", AMBER, s.released, "released / thumbs", CYAN);
        y += 2; Sec(g, x, ref y, "LEARNING");
        Line(g, x, ref y, "model", "v" + s.modelVersion + (s.training ? "  (training…)" : "") + (Core.settings.Learning ? "" : "  (off)"));
        Line(g, x, ref y, "labels", s.labels + "  (" + s.strong + " from you)");
        Line(g, x, ref y, "updates", s.accepted + " accepted · " + s.rejected + " rejected");
        Line(g, x, ref y, "finger size", s.calibrated ? s.fingerW.ToString("F1") + " × " + s.fingerH.ToString("F1") : "calibrating…");
        Line(g, x, ref y, "gestures", Core.gestureOffCount + " holds  (" + Core.lastSpiMs.ToString("F0") + " ms)");
        Line(g, x, ref y, "clicks", Core.thumbTapsIgnored + " taps ignored · " + Core.clicksRecovered + " recovered · " + Core.twoFingerClicks + " right");
        var gs = Core.gest;
        Line(g, x, ref y, "scrolling", !Core.settings.MacScroll ? "Windows" : gs.momentumActive ? "momentum" : gs.mode == Gestures.Mode.Scroll ? "lock: " + gs.axis.ToString().ToLower() : gs.mode == Gestures.Mode.Pinch ? "pinch (Windows)" : "Mac-style · " + gs.lockBreaks + " lock breaks");
        y += 4; Sec(g, x, ref y, "ON THE PAD NOW");
        if (s.contacts.Count == 0) { g.DrawString("nothing", fMed, new SolidBrush(DIM), x, y); y += 20; }
        foreach (var c in s.contacts)
        {
            g.FillEllipse(new SolidBrush(ColOf(c)), x, y + 5, 9, 9);
            g.DrawString(NameOf(c) + "  p " + c.p.ToString("F2") + "  " + (c.age * 1000).ToString("F0") + " ms", fMed, new SolidBrush(TXT), x + 14, y); y += 20;
        }
        Core.CView top = null; foreach (var c in s.contacts) if (c.feat != null && (top == null || c.p > top.p)) top = c;
        if (top != null && y < r.Bottom - 120)
        {
            y += 4; Sec(g, x, ref y, "WHY (top factors)");
            var contrib = new List<KeyValuePair<string, double>>();
            for (int i = 0; i < Model.NF; i++) contrib.Add(new KeyValuePair<string, double>(Model.Names[i], s.weights[i + 1] * top.feat[i]));
            contrib.Sort((a, b) => Math.Abs(b.Value).CompareTo(Math.Abs(a.Value)));
            for (int i = 0; i < 4 && y < r.Bottom - 70; i++)
            {
                var kv = contrib[i]; float bw = (float)Math.Min(70, Math.Abs(kv.Value) * 10);
                g.DrawString(kv.Key, fSmall, new SolidBrush(TXT), x, y);
                g.FillRectangle(new SolidBrush(kv.Value > 0 ? RED : CYAN), r.Right - 90, y + 4, bw, 8); y += 17;
            }
        }
        float ly = r.Bottom - 46;
        Leg(g, x, ly, CYAN, "finger"); Leg(g, x + 70, ly, AMBER, "checking"); Leg(g, x + 150, ly, RED, "palm"); ly += 18;
        Leg(g, x, ly, GREYC, "fw-rejected"); g.DrawArc(new Pen(RED, 2f), x + 92, ly + 3, 10, 10, -90, 270); g.DrawString("palm probability", fSmall, new SolidBrush(TXT), x + 106, ly + 1);
    }
    void Sec(Graphics g, float x, ref float y, string s) { g.DrawString(s, fSmall, new SolidBrush(DIM), x, y); y += 18; }
    void Line(Graphics g, float x, ref float y, string k, string v) { g.DrawString(k, fSmall, new SolidBrush(DIM), x, y + 1); g.DrawString(v, fMed, new SolidBrush(TXT), x + 78, y); y += 19; }
    void Stat2(Graphics g, float x, float half, ref float y, int a, string la, Color ca, int b, string lb, Color cb)
    {
        g.DrawString(a.ToString(), fBig, new SolidBrush(ca), x - 3, y); g.DrawString(b.ToString(), fBig, new SolidBrush(cb), x + half - 3, y); y += 30;
        g.DrawString(la, fSmall, new SolidBrush(DIM), x, y); g.DrawString(lb, fSmall, new SolidBrush(DIM), x + half, y); y += 22;
    }
    void Leg(Graphics g, float x, float y, Color c, string t) { g.FillEllipse(new SolidBrush(c), x, y + 4, 9, 9); g.DrawString(t, fSmall, new SolidBrush(TXT), x + 13, y + 1); }

    void DrawTimeline(Graphics g, RectangleF r)
    {
        using (var p = Round(r, 12)) g.FillPath(new SolidBrush(PANEL), p);
        g.DrawString("LAST 15 s", fSmall, new SolidBrush(DIM), r.X + 12, r.Y + 6);
        Leg(g, r.X + 90, r.Y + 5, CYAN, "finger"); Leg(g, r.X + 160, r.Y + 5, AMBER, "gestures held"); Leg(g, r.X + 270, r.Y + 5, RED, "blocking"); Leg(g, r.X + 350, r.Y + 5, TXT, "keystroke");
        var lane = new RectangleF(r.X + 12, r.Y + 28, r.Width - 24, r.Height - 40); float bw = lane.Width / Core.TL;
        for (int k = 0; k < Core.TL; k++)
        {
            int idx = (Core.tlPos + 1 + k) % Core.TL; float bx = lane.X + k * bw; byte s = Core.tlState[idx];
            if (s > 0) { Color c = s == 3 ? RED : s == 2 ? AMBER : CYAN; float hh = lane.Height * (s == 3 ? 0.7f : s == 2 ? 0.5f : 0.3f); g.FillRectangle(new SolidBrush(Color.FromArgb(200, c)), bx, lane.Bottom - hh, bw + 0.6f, hh); }
            if (Core.tlKey[idx]) g.FillRectangle(new SolidBrush(Color.FromArgb(220, TXT)), bx, lane.Y, Math.Max(1.5f, bw), lane.Height * 0.18f);
        }
        g.DrawLine(new Pen(GRID), lane.X, lane.Bottom, lane.Right, lane.Bottom);
    }
}

// ======================================================================================
// Icon (embedded resource, with a drawn fallback)
// ======================================================================================
static class AppIcon
{
    static Icon normal, paused;
    public static Icon Get(bool isPaused)
    {
        if (normal == null)
        {
            try { using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("PalmGuard.ico")) if (st != null) normal = new Icon(st, 32, 32); } catch { }
            if (normal == null) normal = Draw(Color.FromArgb(56, 214, 255));
            paused = Draw(Color.FromArgb(120, 128, 140));
        }
        return isPaused ? paused : normal;
    }
    static Icon Draw(Color c)
    {
        var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            g.FillRectangle(new SolidBrush(Color.FromArgb(30, 36, 48)), 2, 6, 28, 20);
            g.DrawRectangle(new Pen(c, 2), 2, 6, 28, 20);
            g.FillEllipse(new SolidBrush(c), 9, 11, 9, 9);
        }
        return Icon.FromHandle(b.GetHicon());
    }
}

// ======================================================================================
// Tray application
// ======================================================================================
class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    NotifyIcon tray; Dashboard dash; SynchronizationContext ui;
    ToolStripMenuItem miPause, miLearn, miThumb, miRecover, miGesture, miRecord, miStartup;

    public TrayApp(bool showDashboard)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        dash = new Dashboard();
        Core.Notify = msg => ui.Post(_ => { dash.ShowToast(msg); if (!dash.Visible) tray.ShowBalloonTip(2500, "PalmGuard", msg, ToolTipIcon.Info); }, null);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (s, e) => ShowDash()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        miPause = new ToolStripMenuItem("Pause palm rejection   Ctrl+Alt+P", null, (s, e) => Core.SetEnabled(!Core.settings.Enabled));
        menu.Items.Add(miPause);
        menu.Items.Add("Palm got through   Ctrl+Alt+Z", null, (s, e) => Core.CorrectMissed());
        menu.Items.Add("Finger was ignored   Ctrl+Alt+X", null, (s, e) => Core.CorrectWrong());
        menu.Items.Add(new ToolStripSeparator());
        var opts = new ToolStripMenuItem("Options");
        miLearn = Opt(opts, "Learn from corrections and usage", () => Core.settings.Learning, v => Core.SetLearning(v));
        miThumb = Opt(opts, "Ignore thumb-zone taps", () => Core.settings.ThumbTapIgnore, v => { Core.settings.ThumbTapIgnore = v; Core.settings.Save(); });
        miRecover = Opt(opts, "Recover clicks Windows drops", () => Core.settings.ClickRecovery, v => { Core.settings.ClickRecovery = v; Core.settings.Save(); });
        miGesture = Opt(opts, "Hold scroll/zoom while a palm is down", () => Core.settings.GestureHold, v => { Core.settings.GestureHold = v; Core.settings.Save(); });
        opts.DropDownItems.Add(new ToolStripSeparator());
        Opt(opts, "Mac-style scrolling (axis lock + momentum)", () => Core.settings.MacScroll, v => { Core.settings.MacScroll = v; Core.settings.Save(); Core.ApplyScrollSettings(); });
        Opt(opts, "Scroll momentum", () => Core.settings.Momentum, v => { Core.settings.Momentum = v; Core.settings.Save(); Core.ApplyScrollSettings(); });
        Opt(opts, "Reverse scroll direction", () => Core.settings.ScrollReverse, v => { Core.settings.ScrollReverse = v; Core.settings.Save(); Core.ApplyScrollSettings(); });
        Opt(opts, "Two-finger press = right click", () => Core.settings.TwoFingerClick, v => { Core.settings.TwoFingerClick = v; Core.settings.Save(); });
        opts.DropDownItems.Add(new ToolStripSeparator());
        miRecord = Opt(opts, "Record sessions for tuning (restart to apply)", () => Core.settings.RecordSessions, v => { Core.settings.RecordSessions = v; Core.settings.Save(); });
        menu.Items.Add(opts);
        miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!miStartup.Checked));
        menu.Items.Add(miStartup);
        menu.Items.Add("Open data folder", null, (s, e) => { try { Process.Start("explorer.exe", Core.dataDir); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitThread());
        menu.Opening += (s, e) =>
        {
            miPause.Text = (Core.settings.Enabled ? "Pause" : "Resume") + " palm rejection   Ctrl+Alt+P";
            miStartup.Checked = IsStartup();
            foreach (var it in opts.DropDownItems.OfType<ToolStripMenuItem>()) ((Func<bool>)it.Tag)();
        };

        tray = new NotifyIcon { Icon = AppIcon.Get(false), Text = "PalmGuard", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDash(); };
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        bool lastEnabled = true;
        t.Tick += (s, e) =>
        {
            bool en = Core.settings.Enabled;
            if (en != lastEnabled) { tray.Icon = AppIcon.Get(!en); lastEnabled = en; }
            tray.Text = "PalmGuard " + Core.VERSION + (en ? " - active" : " - paused");
        };
        t.Start();
        if (showDashboard) ShowDash();
    }
    ToolStripMenuItem Opt(ToolStripMenuItem parent, string text, Func<bool> get, Action<bool> set)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (s, e) => { set(!get()); it.Checked = get(); };
        it.Tag = (Func<bool>)(() => { it.Checked = get(); return true; });
        parent.DropDownItems.Add(it); return it;
    }
    public void ShowDash()
    {
        if (!dash.Visible) dash.Show();
        if (dash.WindowState == FormWindowState.Minimized) dash.WindowState = FormWindowState.Normal;
        dash.Activate();
    }
    public void PostShow() { ui.Post(_ => ShowDash(), null); }
    public void PostExit() { ui.Post(_ => ExitThread(), null); }
    public void PostHotkey(int id)
    {
        if (id == 1) Core.SetEnabled(!Core.settings.Enabled);
        else if (id == 2) Core.CorrectMissed();
        else if (id == 3) Core.CorrectWrong();
    }
    static bool IsStartup()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("PalmGuard") != null; } catch { return false; }
    }
    static void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("PalmGuard", "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue("PalmGuard", false);
            }
        }
        catch (Exception e) { Core.Error("startup", e); }
    }
    protected override void ExitThreadCore()
    {
        tray.Visible = false; tray.Dispose();
        base.ExitThreadCore();
    }
}

// ======================================================================================
// Entry point
// ======================================================================================
static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--replay") return Replay.Run(args);
        if (args.Contains("--exit"))
        {
            // ask a running instance to shut down cleanly (restores gestures, releases buttons)
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(@"Local\PalmGuard.Exit", out ev)) { ev.Set(); ev.Dispose(); }
            return 0;
        }

        bool showDash = !args.Contains("--tray");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalmGuard");
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--data") dir = args[i + 1];

        bool created;
        using (var mutex = new Mutex(true, @"Local\PalmGuard.SingleInstance", out created))
        using (var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PalmGuard.Show"))
        using (var exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PalmGuard.Exit"))
        {
            if (!created) { showEvt.Set(); return 0; } // already running: ask it to show the dashboard

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Core.Error("ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { Core.Error("fatal", e.ExceptionObject as Exception ?? new Exception("unknown")); Core.Shutdown(); } catch { } };

            Core.Init(dir, false);
            var app = new TrayApp(showDash);
            InputWindow.Start(id => app.PostHotkey(id));
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume && InputWindow.Instance != null) W32.PostMessage(InputWindow.Instance.Handle, InputWindow.WM_RESUME, IntPtr.Zero, IntPtr.Zero);
            };
            new Thread(() => { while (true) { showEvt.WaitOne(); app.PostShow(); } }) { IsBackground = true, Name = "PalmGuard.Show" }.Start();
            new Thread(() => { exitEvt.WaitOne(); app.PostExit(); }) { IsBackground = true, Name = "PalmGuard.Exit" }.Start();
            SystemEvents.SessionEnding += (s, e) => Core.Shutdown();

            try { Application.Run(app); }
            finally { Core.Shutdown(); }
        }
        return 0;
    }
}

// ======================================================================================
// Replay / demo mode: plays a recorded session through the engine + dashboard (no hooks, no settings changes)
//   PalmGuard.exe --replay <sessionDir> [--from t] [--shot t file.png]... [--frames t0 t1 dt outDir] [--quit t]
// ======================================================================================
static class Replay
{
    public static int Run(string[] args)
    {
        string dir = args[1]; var shots = new List<KeyValuePair<double, string>>();
        double f0 = -1, f1 = -1, fdt = 0.1, quit = -1, from = 0; string fdir = null;
        for (int a = 2; a < args.Length; a++)
        {
            if (args[a] == "--shot") { shots.Add(new KeyValuePair<double, string>(double.Parse(args[a + 1], CultureInfo.InvariantCulture), args[a + 2])); a += 2; }
            else if (args[a] == "--frames") { f0 = double.Parse(args[a + 1], CultureInfo.InvariantCulture); f1 = double.Parse(args[a + 2], CultureInfo.InvariantCulture); fdt = double.Parse(args[a + 3], CultureInfo.InvariantCulture); fdir = args[a + 4]; a += 4; Directory.CreateDirectory(fdir); }
            else if (args[a] == "--quit") quit = double.Parse(args[++a], CultureInfo.InvariantCulture);
            else if (args[a] == "--from") from = double.Parse(args[++a], CultureInfo.InvariantCulture);
        }
        Core.Init(Path.Combine(Path.GetTempPath(), "palmguard-replay"), true);

        var rows = new List<string[]>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "touch.csv")).Skip(1)) { var p = line.Split(','); if (p.Length >= 16) rows.Add(p); }
        var keys = new List<double>(); var kf = Path.Combine(dir, "keys.csv");
        if (File.Exists(kf)) foreach (var line in File.ReadLines(kf).Skip(1)) keys.Add(double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture));

        Application.EnableVisualStyles();
        var f = new Dashboard { replayMode = true, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false, Text = "PalmGuard (replay)" };
        int ri = 0, ki = 0, fn = 0, si = 0; double nextFrame = f0; var clock = new Stopwatch();
        shots.Sort((x, y) => x.Key.CompareTo(y.Key));
        var rt = new System.Windows.Forms.Timer { Interval = 5 };
        rt.Tick += (s, e) =>
        {
            double now = from + clock.Elapsed.TotalSeconds;
            lock (Core.Sync)
            {
                while (ri < rows.Count)
                {
                    double t = double.Parse(rows[ri][0], CultureInfo.InvariantCulture); if (t > now) break;
                    string scan = rows[ri][2]; var frame = new List<Touch>(); bool btn = false;
                    while (ri < rows.Count && rows[ri][2] == scan && double.Parse(rows[ri][0], CultureInfo.InvariantCulture) == t)
                    {
                        var p = rows[ri];
                        frame.Add(new Touch { id = uint.Parse(p[5]), tip = p[6] == "1", conf = p[7] == "1", x = int.Parse(p[8]), y = int.Parse(p[9]), w = int.Parse(p[10]), h = int.Parse(p[11]) });
                        if (p[13] == "1") btn = true; ri++;
                    }
                    while (ki < keys.Count && keys[ki] <= t) Core.eng.OnKey(keys[ki++]);
                    Core.replayT = t;
                    Core.Feed(t, frame, btn);
                }
                Core.replayT = Math.Max(Core.replayT, now);
            }
            if (si < shots.Count && now >= shots[si].Key) { f.Refresh(); f.Shot(shots[si].Value); Console.WriteLine("shot " + shots[si].Value); si++; }
            if (fdir != null && nextFrame >= 0 && now >= nextFrame && now <= f1) { f.Shot(Path.Combine(fdir, string.Format("f{0:D4}.png", fn++))); nextFrame += fdt; }
            if ((quit > 0 && now >= quit) || (quit <= 0 && ri >= rows.Count)) f.Close();
        };
        var ct = new System.Windows.Forms.Timer { Interval = 33 }; ct.Tick += (s, e) => Core.CoreTick();
        f.Shown += (s, e) => { clock.Start(); rt.Start(); ct.Start(); };
        Application.Run(f);
        return 0;
    }
}
