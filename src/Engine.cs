// PalmGuard engine v2: probabilistic, iterative palm classification with hard guard rails
// and self-improving logistic model. Pure logic (no Win32) so it can be replayed offline.
//
// Research basis:
//  - Schwarz et al., CHI'14: temporal features (size stats, deformation, speed, neighbour
//    distance, event count), iterative re-classification during the first ~100-500 ms.
//  - Chromium OS palm_classifying_filter_interpreter: palm release on straight travel >= 8 mm,
//    stationary palm in edge/bottom zone, split-palm proximity, re-evaluation window 100 ms.
//  - libinput: edge exclusion zones with escape-by-motion, disable-while-typing applies to touches
//    *started* while typing, modifiers excluded, sticky palm labels, firmware palm flag trusted.
//  - Apple US7561146: probability from size + trajectory + region + keyboard activity, recomputed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PG
{
    public struct Touch { public uint id; public bool tip, conf; public int x, y, w, h; }

    public class Geom
    {
        public int maxX = 3684, maxY = 2176;
        public double mmX = 117.86 / 3684, mmY = 81.28 / 2176;
        public double Wmm { get { return maxX * mmX; } }
        public double Hmm { get { return maxY * mmY; } }
    }

    public enum Cls { Pending, Finger, Palm }

    public class Config
    {
        public double PalmHRatio = 1.5;                      // hard palm: TALL contact (h >= 1.5x finger and h >= w)
        public double PalmAreaRatio = 3.5;                   // hard palm: huge contact (w*h >= 3.5x finger area)
        public double TypingWRatio = 1.25;                   // hard palm: wide contact started while typing, outside thumb zone
        public double TypingWindow = 1.0;                    // s
        public double ThumbZone = 0.62;                      // contacts below this fraction of pad height are thumb-zone
        public double ClickQuietKeys = 0.5;                  // s without typing for a click to count as deliberate
        public double PendingMax = 0.10;                     // max hesitation for ambiguous touches (s)
        public double FinalAge = 0.50;                       // after this only hard rules change the class
        public double PFingerFast = 0.25, PPalmFast = 0.60;  // early decision band
        public double PDecide = 0.50;                        // decision at PendingMax
        public double PFingerToPalm = 0.85;                  // finger -> palm revision before FinalAge
        public double ReleaseNetMm = 8, ReleaseStraight = 0.8; // Chromium-style release on straight travel
        public double EdgeScaleMm = 6, NeighborScaleMm = 15;
        public double TypingTau = 0.5;                       // s, typing recency decay
        public static readonly double[] CheckpointAges = { 0, 0.025, 0.05, 0.1, 0.2, 0.5 };
    }

    public class Sample { public double t, x, y; public int w, h; public bool conf; }

    public class Contact
    {
        public uint id; public double t0, tEnd = -1;
        public List<Sample> hist = new List<Sample>();
        public Cls cls = Cls.Pending; public double p = 0.5; public string why = "";
        public bool fwRejected, hardPalm, released, final, ended, userLabeled, clicked;
        public double keyAge = 99; public int keyBurst, palmsAtBirth;
        public bool conf = true;
        public int maxW, maxH, firstW, firstH;
        public double sumW, sumH, sumH2, path, minNeighbor = 999;
        public double fwDropAge = -1, palmAge = -1, fingerAge = -1;
        public int nextCp; public List<KeyValuePair<double, double[]>> checkpoints = new List<KeyValuePair<double, double[]>>();
        public double[] lastFeat;
        public Sample First { get { return hist[0]; } }
        public Sample Last { get { return hist[hist.Count - 1]; } }
        public double Net { get { var a = First; var b = Last; return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)); } }
        public bool Blocking { get { return conf && cls != Cls.Finger; } }
    }

    public class LabeledSample { public double[] f; public double y, w; public string src; }

    // ---------------- model ----------------
    public class Model
    {
        public const int NF = 23;
        public static readonly string[] Names = {
            "maxH/finger", "maxW/finger", "area/finger", "size jitter", "size growth",
            "edge side", "edge bottom", "edge top", "net travel", "straightness", "speed",
            "typing recency", "typing burst", "near other touch", "palms present",
            "typing x width", "typing x edge", "age", "stillness", "still x edge",
            "thumb zone", "thumb zone x width", "tallness" };
        public double[] w = new double[NF + 1];
        public int version = 0;

        public static Model Prior()
        {
            // hand-set prior; training pulls toward data but is regularised toward this
            var m = new Model();
            m.w = new double[] { -8.0,
                4.0, 2.0, 1.0, 1.0, 1.0,
                2.5, 0.5, 0.5, -1.0, -1.0, -0.5,
                1.0, 0.5, 1.0, 1.0,
                1.5, 1.0, 0.0, 0.5, 1.5,
                -0.5, -2.0, 1.5 };
            return m;
        }
        public double Z(double[] f) { double z = w[0]; for (int i = 0; i < NF; i++) z += w[i + 1] * f[i]; return z; }
        public double P(double[] f) { return 1.0 / (1.0 + Math.Exp(-Z(f))); }
        public Model Clone() { return new Model { w = (double[])w.Clone(), version = version }; }

        public void Save(string path)
        {
            var sb = new StringBuilder(); sb.AppendLine("version=" + version);
            for (int i = 0; i <= NF; i++) sb.AppendLine((i == 0 ? "bias" : Names[i - 1]) + "=" + w[i].ToString("R", CultureInfo.InvariantCulture));
            File.WriteAllText(path, sb.ToString());
        }
        public static Model Load(string path)
        {
            if (!File.Exists(path)) return null;
            var m = new Model(); var lines = File.ReadAllLines(path);
            if (lines.Length < NF + 2) return null;
            m.version = int.Parse(lines[0].Split('=')[1]);
            for (int i = 0; i <= NF; i++) m.w[i] = double.Parse(lines[i + 1].Split('=')[1], CultureInfo.InvariantCulture);
            return m;
        }
    }

    public static class Trainer
    {
        // L2-regularised (toward prior) class-balanced logistic regression, full-batch GD.
        public static Model Fit(List<LabeledSample> data, Model init, Model prior, double lambda = 0.02, int iters = 3000, double lr = 0.2)
        {
            var m = init.Clone(); int n = data.Count; if (n == 0) return m;
            double wp = 0, wn = 0; foreach (var s in data) { if (s.y > 0.5) wp += s.w; else wn += s.w; }
            double cp = wp > 0 ? (wp + wn) / (2 * wp) : 1, cn = wn > 0 ? (wp + wn) / (2 * wn) : 1, tot = wp + wn;
            var g = new double[Model.NF + 1];
            // Adam
            var mo = new double[Model.NF + 1]; var ve = new double[Model.NF + 1]; double b1 = 0.9, b2 = 0.999;
            for (int it = 1; it <= iters; it++)
            {
                Array.Clear(g, 0, g.Length);
                foreach (var s in data)
                {
                    double sw = s.w * (s.y > 0.5 ? cp : cn) / tot;
                    double e = (m.P(s.f) - s.y) * sw;
                    g[0] += e; for (int i = 0; i < Model.NF; i++) g[i + 1] += e * s.f[i];
                }
                for (int i = 0; i <= Model.NF; i++)
                {
                    g[i] += 2 * lambda * (m.w[i] - prior.w[i]);
                    mo[i] = b1 * mo[i] + (1 - b1) * g[i]; ve[i] = b2 * ve[i] + (1 - b2) * g[i] * g[i];
                    double mh = mo[i] / (1 - Math.Pow(b1, it)), vh = ve[i] / (1 - Math.Pow(b2, it));
                    m.w[i] -= lr * 0.1 * mh / (Math.Sqrt(vh) + 1e-8);
                    m.w[i] = Math.Max(-12, Math.Min(12, m.w[i]));
                }
            }
            return m;
        }
        public class Metrics { public double fingerFP, palmRecall, logloss; public int nF, nP; public override string ToString() { return string.Format(CultureInfo.InvariantCulture, "fingerFP={0:P1} palmRecall={1:P1} logloss={2:F3} (nF={3} nP={4})", fingerFP, palmRecall, logloss, nF, nP); } }
        public static Metrics Eval(List<LabeledSample> data, Model m)
        {
            var r = new Metrics(); double ll = 0, wsum = 0; int fp = 0, tp = 0;
            foreach (var s in data)
            {
                double p = Math.Max(1e-6, Math.Min(1 - 1e-6, m.P(s.f)));
                ll -= s.w * (s.y * Math.Log(p) + (1 - s.y) * Math.Log(1 - p)); wsum += s.w;
                if (s.y > 0.5) { r.nP++; if (p >= 0.5) tp++; } else { r.nF++; if (p >= 0.5) fp++; }
            }
            r.fingerFP = r.nF > 0 ? (double)fp / r.nF : 0; r.palmRecall = r.nP > 0 ? (double)tp / r.nP : 1; r.logloss = wsum > 0 ? ll / wsum : 0;
            return r;
        }
    }

    // ---------------- self-calibration of finger size ----------------
    // Contact width/height units are vendor specific. The model was trained on a reference device whose
    // typical deliberate finger has max size REF_W x REF_H; features divide by fingerW/H = BASE*median/REF,
    // so a typical finger always maps to ~0.75 regardless of the device's units.
    // Until CALIB_MIN deliberate finger strokes have been seen on a new device, size-based rules are
    // disabled and size features are neutral (uncalibrated mode).
    public class Calib
    {
        public const double BASE = 4, REF_W = 3, REF_H = 3; public const int CALIB_MIN = 8;
        public double fingerW = BASE, fingerH = BASE;
        public bool calibrated;
        List<int> ws = new List<int>(), hs = new List<int>();
        public int Samples { get { return ws.Count; } }
        public void AssumeReference() { fingerW = fingerH = BASE; calibrated = true; }
        public void Add(int w, int h)
        {
            if (w <= 0 || h <= 0) return; // device does not report contact size
            ws.Add(w); hs.Add(h); if (ws.Count > 64) { ws.RemoveAt(0); hs.RemoveAt(0); }
            Recompute();
        }
        void Recompute()
        {
            if (ws.Count < CALIB_MIN) return;
            fingerW = Math.Max(0.5, BASE * Median(ws) / REF_W); fingerH = Math.Max(0.5, BASE * Median(hs) / REF_H); calibrated = true;
        }
        static double Median(List<int> l) { var s = l.OrderBy(v => v).ToList(); int n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0; }
        public void Save(string p) { try { File.WriteAllText(p, string.Join(",", ws) + Environment.NewLine + string.Join(",", hs)); } catch { } }
        public void Load(string p)
        {
            try
            {
                if (!File.Exists(p)) return; var l = File.ReadAllLines(p); if (l.Length < 2 || l[0].Length == 0) return;
                foreach (var s in l[0].Split(',')) ws.Add(int.Parse(s)); foreach (var s in l[1].Split(',')) hs.Add(int.Parse(s));
                Recompute();
            }
            catch { ws.Clear(); hs.Clear(); }
        }
    }

    // ---------------- engine ----------------
    public class Engine
    {
        public Config cfg = new Config();
        public Geom geom = new Geom();
        public volatile Model model = Model.Prior();
        public Model prior = Model.Prior();
        public Calib calib = new Calib();
        public Dictionary<uint, Contact> Active = new Dictionary<uint, Contact>();
        public List<Contact> Recent = new List<Contact>();
        public List<double> keyTimes = new List<double>();
        public double lastKey = -99;
        public bool learn = true;
        public Action<LabeledSample> OnLabel;          // weak/strong labels for self-training
        public Action<Contact, string> OnDecision;      // logging hook
        public int palmsModel, palmsHard, palmsFw, released, fingers;

        public void OnKey(double t)
        {
            lastKey = t; keyTimes.Add(t);
            while (keyTimes.Count > 0 && t - keyTimes[0] > 2.0) keyTimes.RemoveAt(0);
        }

        double Mx(int x) { return x * geom.mmX; }
        double My(int y) { return y * geom.mmY; }

        bool prevButton;
        public int thumbClicks;

        bool InThumbZone(Contact c) { return c.Last.y >= cfg.ThumbZone * geom.Hmm; }

        // A deliberate click (physical button or tap reported to the mouse hook) while blocking:
        // release the lowest non-hard-palm contact in the thumb zone if the user isn't typing.
        // Returns true if nothing is blocking afterwards (so the click may pass).
        public bool OnClick(double t)
        {
            if (t - lastKey >= cfg.ClickQuietKeys)
            {
                Contact best = null;
                foreach (var c in Active.Values)
                    if (c.Blocking && !c.hardPalm && InThumbZone(c) && (best == null || c.Last.y > best.Last.y)) best = c;
                if (best != null)
                {
                    best.clicked = true; best.final = true; thumbClicks++;
                    Set(best, Cls.Finger, "thumb click", t);
                }
            }
            string r; return !ShouldBlock(out r);
        }

        public void OnFrame(double t, IList<Touch> touches, bool button)
        {
            bool press = button && !prevButton; prevButton = button;
            OnFrame(t, touches);
            if (press) { foreach (var c in Active.Values) if (!c.Blocking) c.clicked = true; OnClick(t); }
        }

        public void OnFrame(double t, IList<Touch> touches)
        {
            var seen = new HashSet<uint>();
            foreach (var tc in touches)
            {
                Contact c;
                if (!tc.tip) { if (Active.TryGetValue(tc.id, out c)) End(c, t); continue; }
                seen.Add(tc.id);
                if (!Active.TryGetValue(tc.id, out c))
                {
                    c = new Contact { id = tc.id, t0 = t, keyAge = t - lastKey, firstW = tc.w, firstH = tc.h };
                    int burst = 0; foreach (var k in keyTimes) if (t - k <= 2.0) burst++;
                    c.keyBurst = burst;
                    foreach (var o in Active.Values) if (o.cls == Cls.Palm) c.palmsAtBirth++;
                    Active[tc.id] = c;
                }
                var s = new Sample { t = t, x = Mx(tc.x), y = My(tc.y), w = tc.w, h = tc.h, conf = tc.conf };
                if (c.hist.Count > 0) { var l = c.Last; c.path += Math.Sqrt((s.x - l.x) * (s.x - l.x) + (s.y - l.y) * (s.y - l.y)); }
                c.hist.Add(s); c.conf = tc.conf;
                c.maxW = Math.Max(c.maxW, tc.w); c.maxH = Math.Max(c.maxH, tc.h);
                c.sumW += tc.w; c.sumH += tc.h; c.sumH2 += tc.h * tc.h;
                if (!tc.conf && !c.fwRejected) { c.fwRejected = true; c.fwDropAge = t - c.t0; }
            }
            // drop vanished contacts
            var gone = new List<Contact>(); foreach (var c in Active.Values) if (!seen.Contains(c.id)) gone.Add(c);
            foreach (var c in gone) End(c, t);
            // neighbour distances
            foreach (var a in Active.Values)
                foreach (var b in Active.Values)
                    if (a != b) { double d = Dist(a.Last, b.Last); if (d < a.minNeighbor) a.minNeighbor = d; }
            foreach (var c in Active.Values) Update(c, t);
            while (Recent.Count > 0 && t - Recent[0].tEnd > 6) Recent.RemoveAt(0);
        }

        static double Dist(Sample a, Sample b) { return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)); }

        public double[] Features(Contact c, double t)
        {
            var f = new double[Model.NF];
            double fw = calib.fingerW, fh = calib.fingerH; int n = c.hist.Count;
            double age = t - c.t0, net = c.Net, meanH = c.sumH / n, varH = Math.Max(0, c.sumH2 / n - meanH * meanH);
            var L = c.Last;
            double dSide = Math.Min(L.x, geom.Wmm - L.x), dBottom = geom.Hmm - L.y, dTop = L.y;
            // use the most edge-ward position seen at birth too (palms often start hanging off the edge)
            dSide = Math.Min(dSide, Math.Min(c.First.x, geom.Wmm - c.First.x));
            double typing = Math.Exp(-Math.Max(0, c.keyAge) / cfg.TypingTau);
            double edge = Math.Exp(-dSide / cfg.EdgeScaleMm);
            double still = Math.Exp(-net / 2.0) * Math.Min(1, age / 0.3);
            bool cal = calib.calibrated;
            f[0] = cal ? c.maxH / fh : 0.75;
            f[1] = cal ? c.maxW / fw : 0.75;
            f[2] = cal ? (c.sumW / n) * (meanH) / (fw * fh) : 0.56;
            f[3] = cal ? Math.Sqrt(varH) / fh : 0.05;
            f[4] = cal ? (c.maxH - c.firstH) / fh : 0;
            f[5] = edge;
            f[6] = Math.Exp(-dBottom / cfg.EdgeScaleMm);
            f[7] = Math.Exp(-dTop / cfg.EdgeScaleMm);
            f[8] = Math.Log(1 + net);
            f[9] = n > 1 ? net / (c.path + 0.5) : 0;
            f[10] = Math.Log(1 + (age > 0.005 ? c.path / age : 0)) / 5;
            f[11] = typing;
            f[12] = Math.Min(c.keyBurst, 10) / 10.0;
            f[13] = c.minNeighbor < 999 ? Math.Exp(-c.minNeighbor / cfg.NeighborScaleMm) : 0;
            f[14] = Math.Min(c.palmsAtBirth, 2) / 2.0;
            f[15] = typing * f[1];
            f[16] = typing * edge;
            f[17] = Math.Min(age, 0.5) / 0.5;
            f[18] = still;
            f[19] = still * edge;
            double tz = 1.0 / (1.0 + Math.Exp(-(c.First.y - cfg.ThumbZone * geom.Hmm) / 3.0));
            f[20] = tz;
            f[21] = tz * f[1];
            f[22] = cal ? (c.maxH - c.maxW) / fh : 0;
            return f;
        }

        void Set(Contact c, Cls k, string why, double t)
        {
            if (c.cls == k) return;
            c.cls = k; c.why = why;
            if (k == Cls.Palm && c.palmAge < 0) c.palmAge = t - c.t0;
            if (k == Cls.Finger && c.fingerAge < 0) c.fingerAge = t - c.t0;
            if (OnDecision != null) OnDecision(c, why);
        }

        void Update(Contact c, double t)
        {
            double age = t - c.t0;
            var f = Features(c, t); c.lastFeat = f;
            var m = model; c.p = m.P(f);
            while (c.nextCp < Config.CheckpointAges.Length && age >= Config.CheckpointAges[c.nextCp])
            { c.checkpoints.Add(new KeyValuePair<double, double[]>(age, f)); c.nextCp++; }

            // hard palm guard rails (always win)
            if (!c.hardPalm)
            {
                string hard = null;
                if (c.fwRejected) { hard = "firmware"; palmsFw++; }
                else if (c.clicked) { }
                else if (!calib.calibrated) { }
                else if (c.maxH >= cfg.PalmHRatio * calib.fingerH && c.maxH >= c.maxW) { hard = "tall W" + c.maxW + " H" + c.maxH; palmsHard++; }
                else if (c.maxW * c.maxH >= cfg.PalmAreaRatio * calib.fingerW * calib.fingerH) { hard = "huge W" + c.maxW + " H" + c.maxH; palmsHard++; }
                else if (c.maxW >= cfg.TypingWRatio * calib.fingerW && c.keyAge < cfg.TypingWindow && c.First.y < cfg.ThumbZone * geom.Hmm) { hard = "wide while typing W" + c.maxW; palmsHard++; }
                if (hard != null) { c.hardPalm = true; c.final = true; Set(c, Cls.Palm, hard, t); return; }
            }
            else return;

            // release: straight deliberate travel with finger-like size (Chromium fat-finger release)
            if (c.cls == Cls.Palm || c.cls == Cls.Pending)
            {
                double net = c.Net;
                if (net >= cfg.ReleaseNetMm && net / (c.path + 0.5) >= cfg.ReleaseStraight)
                { c.released = true; c.final = true; released++; Set(c, Cls.Finger, "released: straight travel " + net.ToString("F0") + "mm", t); return; }
            }
            if (c.final) return;

            if (c.cls == Cls.Pending)
            {
                if (c.p >= cfg.PPalmFast) { palmsModel++; Set(c, Cls.Palm, "model p=" + c.p.ToString("F2"), t); }
                else if (c.p <= cfg.PFingerFast) { fingers++; Set(c, Cls.Finger, "model p=" + c.p.ToString("F2"), t); }
                else if (age >= cfg.PendingMax)
                {
                    if (c.p >= cfg.PDecide) { palmsModel++; Set(c, Cls.Palm, "model p=" + c.p.ToString("F2") + " @100ms", t); }
                    else { fingers++; Set(c, Cls.Finger, "model p=" + c.p.ToString("F2") + " @100ms", t); }
                }
            }
            else if (c.cls == Cls.Finger && c.p >= cfg.PFingerToPalm) { palmsModel++; Set(c, Cls.Palm, "revised p=" + c.p.ToString("F2"), t); }

            if (age >= cfg.FinalAge) c.final = true;
        }

        void End(Contact c, double t)
        {
            c.ended = true; c.tEnd = t; Active.Remove(c.id);
            if (c.hist.Count == 0) return;
            if (c.cls == Cls.Pending) { Set(c, c.p >= cfg.PDecide ? Cls.Palm : Cls.Finger, "lifted p=" + c.p.ToString("F2"), t); }
            Recent.Add(c);
            if (!learn) return;
            // weak labels -------------------------------------------------
            // 1) firmware later flagged it a palm -> early checkpoints were palm
            if (c.fwRejected && c.fwDropAge > 0)
                foreach (var cp in c.checkpoints) if (cp.Key < c.fwDropAge) Emit(cp.Value, 1, 0.5, "fw-late");
            // 2) size grew past the hard palm threshold later -> teach early recognition
            if (c.hardPalm && !c.fwRejected && c.palmAge > 0)
                foreach (var cp in c.checkpoints) if (cp.Key < c.palmAge) Emit(cp.Value, 1, 0.4, "size-late");
            // 2b) deliberate click by a contact we had doubts about -> it was a finger/thumb
            if (c.clicked && !c.hardPalm && !c.fwRejected)
                foreach (var cp in c.checkpoints) Emit(cp.Value, 0, 0.5, "thumb-click");
            // 3) clearly deliberate finger: long travel, no typing around, finger-like size, not rejected
            double net = c.Net;
            if (c.cls == Cls.Finger && !c.fwRejected && !c.hardPalm && c.path >= 20 && c.keyAge > 1.0 && !c.clicked
                && (!calib.calibrated || (c.maxH < 1.25 * calib.fingerH && c.maxW < 1.15 * calib.fingerW)))
            {
                foreach (var cp in c.checkpoints) Emit(cp.Value, 0, 0.3, "finger-travel");
                calib.Add(c.maxW, c.maxH);
            }
            // 4) stationary edge contact that lingered (Chromium stationary palm) and never moved
            if (!c.hardPalm && net < 3 && t - c.t0 > 1.5 && c.lastFeat != null && c.lastFeat[5] > 0.5)
                foreach (var cp in c.checkpoints) Emit(cp.Value, 1, 0.3, "edge-still");
        }

        void Emit(double[] f, double y, double w, string src)
        {
            if (OnLabel != null) OnLabel(new LabeledSample { f = f, y = y, w = w, src = src });
        }

        // user corrections (strong labels) -----------------------------------
        public int MarkMissedPalm(double t, double window = 3.0)
        {
            int n = 0;
            foreach (var c in AllRecent(t, window))
                if (c.cls == Cls.Finger && !c.userLabeled)
                {
                    c.userLabeled = true; n++;
                    foreach (var cp in c.checkpoints) Emit(cp.Value, 1, 1.0, "user-palm");
                    if (!c.ended) { c.final = true; Set(c, Cls.Palm, "user marked palm", t); }
                }
            return n;
        }
        public int MarkFalsePalm(double t, double window = 3.0)
        {
            int n = 0;
            foreach (var c in AllRecent(t, window))
                if (c.cls != Cls.Finger && !c.fwRejected && !c.userLabeled)
                {
                    c.userLabeled = true; n++;
                    foreach (var cp in c.checkpoints) Emit(cp.Value, 0, 1.0, "user-finger");
                }
            return n;
        }
        IEnumerable<Contact> AllRecent(double t, double window)
        {
            foreach (var c in Active.Values) yield return c;
            foreach (var c in Recent) if (t - c.tEnd <= window) yield return c;
        }

        public bool ShouldBlock(out string reason)
        {
            foreach (var c in Active.Values) if (c.Blocking) { reason = c.cls == Cls.Pending ? "checking…" : c.why; return true; }
            reason = ""; return false;
        }
    }

    // ---------------- label store + background self-training ----------------
    public class Learner
    {
        public List<LabeledSample> seed = new List<LabeledSample>();
        public List<LabeledSample> live = new List<LabeledSample>();
        public int sinceTrain = 0, accepted = 0, rejected = 0;
        public string lastResult = "not trained yet";
        string dir; readonly object lk = new object();
        public volatile bool training;

        public Learner(string dir) { this.dir = dir; Directory.CreateDirectory(dir); }

        public static string Ser(LabeledSample s)
        {
            return s.y.ToString(CultureInfo.InvariantCulture) + "," + s.w.ToString(CultureInfo.InvariantCulture) + "," + s.src + "," +
                string.Join(",", s.f.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        }
        public static LabeledSample De(string line)
        {
            var p = line.Split(','); if (p.Length != 3 + Model.NF) return null;
            return new LabeledSample { y = double.Parse(p[0], CultureInfo.InvariantCulture), w = double.Parse(p[1], CultureInfo.InvariantCulture), src = p[2], f = p.Skip(3).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray() };
        }
        public static List<LabeledSample> LoadFile(string path)
        {
            var l = new List<LabeledSample>(); if (!File.Exists(path)) return l;
            foreach (var line in File.ReadAllLines(path)) { var s = De(line); if (s != null) l.Add(s); }
            return l;
        }
        public void Load()
        {
            seed = LoadFile(Path.Combine(dir, "seed.csv"));
            live = LoadFile(Path.Combine(dir, "labels.csv"));
        }
        public void Add(LabeledSample s)
        {
            lock (lk) { live.Add(s); sinceTrain++; if (live.Count > 30000) live.RemoveRange(0, live.Count - 30000); }
            try { File.AppendAllText(Path.Combine(dir, "labels.csv"), Ser(s) + "\n"); } catch { }
        }
        public int StrongCount { get { lock (lk) return live.Count(s => s.w >= 1.0); } }

        // Train on seed + live; accept only if no regression on validation (seed + strong user labels).
        public Model TrainAndGate(Model current, Model prior, Action<string> log)
        {
            List<LabeledSample> data, val;
            lock (lk) { data = seed.Concat(live).ToList(); val = seed.Concat(live.Where(s => s.w >= 1.0)).ToList(); sinceTrain = 0; }
            if (data.Count < 20) { lastResult = "too few samples"; return null; }
            var cand = Trainer.Fit(data, current, prior);
            var a = Trainer.Eval(val, current); var b = Trainer.Eval(val, cand);
            bool ok = b.fingerFP <= a.fingerFP + 0.005 && b.logloss <= a.logloss + 1e-3;
            lastResult = (ok ? "accepted" : "rejected") + "  old: " + a + "  new: " + b;
            if (log != null) log(lastResult);
            if (!ok) { rejected++; return null; }
            accepted++; cand.version = current.version + 1; return cand;
        }
    }
}
