using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

public static class PtpLog
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page; public ushort Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint sz);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(IntPtr h, uint c, IntPtr d, ref uint sz);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint val, IntPtr pp, byte[] rpt, uint len);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] list, ref uint len, IntPtr pp, byte[] rpt, uint len2);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);

    const int OK = 0x110000;
    static Dictionary<IntPtr, IntPtr> ppCache = new Dictionary<IntPtr, IntPtr>();
    static Dictionary<IntPtr, List<ushort>> fingerLinks = new Dictionary<IntPtr, List<ushort>>();
    static Dictionary<IntPtr, bool> isPad = new Dictionary<IntPtr, bool>();
    static HashSet<string> seenNames = new HashSet<string>();
    static StreamWriter touchOut, keyOut, rawOut;
    static Stopwatch sw = new Stopwatch();
    static int hidMsgs = 0, padRows = 0;
    static string[] phaseNames = { "ready", "A_one_finger", "B_typing_palms", "C_two_finger_scroll", "D_palm_still", "E_palm_plus_finger" };
    static string[] instr = {
        "Get ready: hands OFF the touchpad",
        "1/5  ONE FINGER: move the cursor around normally",
        "2/5  TYPE in the box below, palms resting like when the cursor jumps",
        "3/5  TWO-FINGER SCROLL up and down",
        "4/5  PALM RESTING STILL on the pad (no typing)",
        "5/5  PALM RESTING + ONE FINGER moving the cursor" };
    static double[] phaseStart = { 0, 5, 20, 40, 52, 62 };
    const double END = 75;

    static string Phase(double t) { int p = 0; for (int i = 0; i < phaseStart.Length; i++) if (t >= phaseStart[i]) p = i; return phaseNames[p]; }

    static string DevName(IntPtr dev)
    {
        uint sz = 0; GetRawInputDeviceInfo(dev, 0x20000007, IntPtr.Zero, ref sz);
        IntPtr nb = Marshal.AllocHGlobal((int)sz * 2 + 2);
        GetRawInputDeviceInfo(dev, 0x20000007, nb, ref sz);
        string s = Marshal.PtrToStringUni(nb) ?? ""; Marshal.FreeHGlobal(nb); return s;
    }

    static bool CheckPad(IntPtr dev)
    {
        bool r;
        if (isPad.TryGetValue(dev, out r)) return r;
        string name = DevName(dev); seenNames.Add(name);
        r = name.IndexOf("BLTP784", StringComparison.OrdinalIgnoreCase) >= 0;
        if (r)
        {
            uint sz = 0; GetRawInputDeviceInfo(dev, 0x20000005, IntPtr.Zero, ref sz);
            IntPtr pp = Marshal.AllocHGlobal((int)sz);
            GetRawInputDeviceInfo(dev, 0x20000005, pp, ref sz);
            ppCache[dev] = pp;
            ushort n = 256; var vc = new byte[72 * n];
            var links = new List<ushort>();
            if (HidP_GetValueCaps(0, vc, ref n, pp) == OK)
                for (int i = 0; i < n; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(vc, o), link = BitConverter.ToUInt16(vc, o + 6), usage = BitConverter.ToUInt16(vc, o + 56);
                    if (page == 0x0D && usage == 0x51 && !links.Contains(link)) links.Add(link);
                }
            fingerLinks[dev] = links;
            Console.WriteLine("touchpad: " + name + "  finger slots=" + links.Count);
        }
        isPad[dev] = r;
        return r;
    }

    static uint Val(IntPtr pp, byte[] rpt, ushort page, ushort link, ushort usage)
    {
        uint v; return HidP_GetUsageValue(0, page, link, usage, out v, pp, rpt, (uint)rpt.Length) == OK ? v : 0xFFFFFFFF;
    }

    static void Handle(IntPtr lp)
    {
        uint sz = 0, hdr = (uint)(8 + IntPtr.Size * 2);
        GetRawInputData(lp, 0x10000003, IntPtr.Zero, ref sz, hdr);
        IntPtr buf = Marshal.AllocHGlobal((int)sz);
        try
        {
            GetRawInputData(lp, 0x10000003, buf, ref sz, hdr);
            uint type = (uint)Marshal.ReadInt32(buf);
            IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            double t = sw.Elapsed.TotalSeconds;
            if (type == 1)
            {
                ushort flags = (ushort)Marshal.ReadInt16(buf, (int)hdr + 2);
                ushort vk = (ushort)Marshal.ReadInt16(buf, (int)hdr + 6);
                if ((flags & 1) == 0) keyOut.WriteLine("{0:F4},{1},{2}", t, Phase(t), vk);
                return;
            }
            if (type != 2) return;
            hidMsgs++;
            { int sh = Marshal.ReadInt32(buf, (int)hdr), ch = Marshal.ReadInt32(buf, (int)hdr + 4); var all = new byte[sh * ch]; Marshal.Copy(buf + (int)hdr + 8, all, 0, all.Length); rawOut.WriteLine("{0:F4},{1},{2},{3}", t, Phase(t), sh, BitConverter.ToString(all).Replace("-", "")); }
            if (!CheckPad(dev)) return;
            int sizeHid = Marshal.ReadInt32(buf, (int)hdr), count = Marshal.ReadInt32(buf, (int)hdr + 4);
            IntPtr pp = ppCache[dev];
            POINT cur; GetCursorPos(out cur);
            for (int r = 0; r < count; r++)
            {
                var rpt = new byte[sizeHid];
                Marshal.Copy(buf + (int)hdr + 8 + r * sizeHid, rpt, 0, sizeHid);
                uint ccount = Val(pp, rpt, 0x0D, 0, 0x54), scan = Val(pp, rpt, 0x0D, 0, 0x56);
                var btn = new ushort[16]; uint bl = 16;
                bool click = HidP_GetUsages(0, 0x09, 0, btn, ref bl, pp, rpt, (uint)rpt.Length) == OK && bl > 0;
                foreach (ushort link in fingerLinks[dev])
                {
                    var us = new ushort[16]; uint ul = 16;
                    bool tip = false, conf = false;
                    if (HidP_GetUsages(0, 0x0D, link, us, ref ul, pp, rpt, (uint)rpt.Length) == OK)
                        for (int i = 0; i < ul; i++) { if (us[i] == 0x42) tip = true; if (us[i] == 0x47) conf = true; }
                    uint id = Val(pp, rpt, 0x0D, link, 0x51);
                    uint x = Val(pp, rpt, 0x01, link, 0x30), y = Val(pp, rpt, 0x01, link, 0x31);
                    uint w = Val(pp, rpt, 0x0D, link, 0x48), h = Val(pp, rpt, 0x0D, link, 0x49), pr = Val(pp, rpt, 0x0D, link, 0x30);
                    if (!tip && !conf && x == 0 && y == 0 && w == 0 && h == 0) continue;
                    padRows++;
                    touchOut.WriteLine("{0:F4},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15}",
                        t, Phase(t), scan, ccount, link, id, tip ? 1 : 0, conf ? 1 : 0, x, y, w, h, pr, click ? 1 : 0, cur.X, cur.Y);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    class F : Form
    {
        public Label step, timer; public TextBox box;
        public F()
        {
            Text = "Touchpad logger"; Width = 1000; Height = 450; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            step = new Label { Dock = DockStyle.Top, Height = 180, Font = new Font("Segoe UI", 24, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            timer = new Label { Dock = DockStyle.Top, Height = 50, Font = new Font("Segoe UI", 14), TextAlign = ContentAlignment.MiddleCenter };
            box = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 12) };
            Controls.Add(box); Controls.Add(timer); Controls.Add(step);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF) { try { Handle(m.LParam); } catch (Exception e) { Console.Error.WriteLine(e); } }
            base.WndProc(ref m);
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        string dir = args[0];
        touchOut = new StreamWriter(Path.Combine(dir, "touch.csv"));
        keyOut = new StreamWriter(Path.Combine(dir, "keys.csv"));
        rawOut = new StreamWriter(Path.Combine(dir, "raw.csv"));
        touchOut.WriteLine("t,phase,scan,contact_count,slot,id,tip,confidence,x,y,width,height,pressure,button,cur_x,cur_y");
        keyOut.WriteLine("t,phase,vk");
        var f = new F();
        int last = -1;
        var tm = new Timer { Interval = 100 };
        f.Shown += (s, e) =>
        {
            var devs = new[] {
                new RAWINPUTDEVICE { Page = 0x0D, Usage = 0x05, Flags = 0x100, Target = f.Handle },
                new RAWINPUTDEVICE { Page = 0x01, Usage = 0x06, Flags = 0x100, Target = f.Handle } };
            if (!RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) Console.WriteLine("register failed " + Marshal.GetLastWin32Error());
            f.Activate(); f.box.Focus();
            sw.Start(); tm.Start();
        };
        tm.Tick += (s, e) =>
        {
            double t = sw.Elapsed.TotalSeconds;
            if (t >= END)
            {
                tm.Stop(); System.Media.SystemSounds.Asterisk.Play();
                f.step.Text = "DONE - thanks!"; f.step.BackColor = Color.LightGreen; f.timer.Text = "";
                var c = new Timer { Interval = 1500 }; c.Tick += (a, b) => f.Close(); c.Start(); return;
            }
            int p = 0; for (int i = 0; i < phaseStart.Length; i++) if (t >= phaseStart[i]) p = i;
            double nxt = p + 1 < phaseStart.Length ? phaseStart[p + 1] : END;
            if (p != last)
            {
                last = p; f.step.Text = instr[p]; f.step.BackColor = p % 2 == 0 ? Color.LightYellow : Color.LightSkyBlue;
                if (p > 0) System.Media.SystemSounds.Exclamation.Play();
                Console.WriteLine("{0:F1}s -> {1}", t, phaseNames[p]);
            }
            f.timer.Text = string.Format("next step in {0:F0}s   |   touchpad packets: {1}", Math.Ceiling(nxt - t), padRows);
        };
        Application.Run(f);
        touchOut.Close(); keyOut.Close(); rawOut.Close();
        Console.WriteLine("hid msgs=" + hidMsgs + " pad rows=" + padRows);
        foreach (var n in seenNames) Console.WriteLine("hid dev: " + n);
    }
}
