// Read-only probe: records vendor-defined (0xFF00) input reports from the BLTP784 touchpad
// alongside PTP button state and lowest-contact size, with on-screen step cues.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class Probe
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort Page; public ushort Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint sz);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(IntPtr h, uint c, IntPtr d, ref uint sz);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint val, IntPtr pp, byte[] rpt, uint len);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] list, ref uint len, IntPtr pp, byte[] rpt, uint len2);
    const int OK = 0x110000;

    static Stopwatch sw = new Stopwatch();
    static StreamWriter outp;
    static Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();
    static Dictionary<IntPtr, IntPtr> pps = new Dictionary<IntPtr, IntPtr>();
    static int vendorCount = 0, ptpCount = 0;
    static string[] phaseNames = { "ready", "A_rest", "B_light_pulses", "C_click_hold_ease", "D_firm_clicks", "E_lift" };
    static string[] instr = {
        "Get ready: hands OFF the touchpad",
        "1/5  REST your thumb on the bottom-middle, no pressure",
        "2/5  Thumb resting: press LIGHTLY a few times (don't click)",
        "3/5  Press until it CLICKS, hold, then EASE OFF slowly (thumb stays on)",
        "4/5  FIRM clicks, several times, thumb stays on",
        "5/5  LIFT your thumb off" };
    static double[] phaseStart = { 0, 5, 15, 25, 37, 47 };
    const double END = 52;
    static string Phase(double t) { int p = 0; for (int i = 0; i < phaseStart.Length; i++) if (t >= phaseStart[i]) p = i; return phaseNames[p]; }

    static string Name(IntPtr dev)
    {
        string n; if (names.TryGetValue(dev, out n)) return n;
        uint sz = 0; GetRawInputDeviceInfo(dev, 0x20000007, IntPtr.Zero, ref sz);
        IntPtr nb = Marshal.AllocHGlobal((int)sz * 2 + 2); GetRawInputDeviceInfo(dev, 0x20000007, nb, ref sz);
        n = Marshal.PtrToStringUni(nb) ?? ""; Marshal.FreeHGlobal(nb);
        sz = 0; GetRawInputDeviceInfo(dev, 0x20000005, IntPtr.Zero, ref sz);
        IntPtr pp = Marshal.AllocHGlobal((int)sz); GetRawInputDeviceInfo(dev, 0x20000005, pp, ref sz); pps[dev] = pp;
        names[dev] = n; return n;
    }

    static void Handle(IntPtr lp)
    {
        uint sz = 0, hdr = (uint)(8 + IntPtr.Size * 2);
        GetRawInputData(lp, 0x10000003, IntPtr.Zero, ref sz, hdr);
        IntPtr buf = Marshal.AllocHGlobal((int)sz);
        try
        {
            GetRawInputData(lp, 0x10000003, buf, ref sz, hdr);
            if (Marshal.ReadInt32(buf) != 2) return;
            IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            string n = Name(dev);
            if (n.IndexOf("BLTP784", StringComparison.OrdinalIgnoreCase) < 0) return;
            int sh = Marshal.ReadInt32(buf, (int)hdr), cnt = Marshal.ReadInt32(buf, (int)hdr + 4);
            double t = sw.Elapsed.TotalSeconds;
            for (int r = 0; r < cnt; r++)
            {
                var rpt = new byte[sh]; Marshal.Copy(buf + (int)hdr + 8 + r * sh, rpt, 0, sh);
                if (n.IndexOf("Col02", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ptpCount++;
                    var pp = pps[dev];
                    var bu = new ushort[8]; uint bl = 8;
                    bool btn = HidP_GetUsages(0, 0x09, 0, bu, ref bl, pp, rpt, (uint)rpt.Length) == OK && bl > 0;
                    uint w = 0, h = 0, y = 0, cc = 0; HidP_GetUsageValue(0, 0x0D, 0, 0x54, out cc, pp, rpt, (uint)rpt.Length);
                    // first slot is enough for single-thumb test
                    HidP_GetUsageValue(0, 0x0D, 1, 0x48, out w, pp, rpt, (uint)rpt.Length);
                    HidP_GetUsageValue(0, 0x0D, 1, 0x49, out h, pp, rpt, (uint)rpt.Length);
                    HidP_GetUsageValue(0, 0x01, 1, 0x31, out y, pp, rpt, (uint)rpt.Length);
                    outp.WriteLine("{0:F4},{1},ptp,{2},{3},{4},{5},{6}", t, Phase(t), btn ? 1 : 0, cc, w, h, y);
                }
                else
                {
                    vendorCount++;
                    outp.WriteLine("{0:F4},{1},vendor,{2},{3}", t, Phase(t), rpt[0], BitConverter.ToString(rpt).Replace("-", ""));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    class F : Form
    {
        public Label step, info;
        public F()
        {
            Text = "Touchpad force probe (read-only)"; Width = 1000; Height = 360; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            step = new Label { Dock = DockStyle.Top, Height = 200, Font = new Font("Segoe UI", 22, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            info = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13), TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(info); Controls.Add(step);
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
        outp = new StreamWriter(Path.Combine(args[0], "probe.csv")); outp.WriteLine("t,phase,src,a,b,c,d,e");
        var f = new F(); int last = -1;
        var tm = new Timer { Interval = 100 };
        f.Shown += (s, e) =>
        {
            var devs = new[] {
                new RAWINPUTDEVICE { Page = 0xFF00, Usage = 0x01, Flags = 0x100, Target = f.Handle },
                new RAWINPUTDEVICE { Page = 0x0D, Usage = 0x05, Flags = 0x100, Target = f.Handle } };
            if (!RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) Console.WriteLine("register failed " + Marshal.GetLastWin32Error());
            f.Activate(); sw.Start(); tm.Start();
        };
        tm.Tick += (s, e) =>
        {
            double t = sw.Elapsed.TotalSeconds;
            if (t >= END) { tm.Stop(); System.Media.SystemSounds.Asterisk.Play(); f.step.Text = "DONE - thanks!"; f.step.BackColor = Color.LightGreen; var c = new Timer { Interval = 1500 }; c.Tick += (a, b) => f.Close(); c.Start(); return; }
            int p = 0; for (int i = 0; i < phaseStart.Length; i++) if (t >= phaseStart[i]) p = i;
            double nxt = p + 1 < phaseStart.Length ? phaseStart[p + 1] : END;
            if (p != last) { last = p; f.step.Text = instr[p]; f.step.BackColor = p % 2 == 0 ? Color.LightYellow : Color.LightSkyBlue; if (p > 0) System.Media.SystemSounds.Exclamation.Play(); }
            f.info.Text = string.Format("next step in {0:F0}s   |   touchpad packets: {1}   |   vendor packets: {2}", Math.Ceiling(nxt - t), ptpCount, vendorCount);
        };
        Application.Run(f);
        outp.Close();
        Console.WriteLine("ptp=" + ptpCount + " vendor=" + vendorCount);
    }
}
