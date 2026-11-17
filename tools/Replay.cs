// Replay harness: feeds a recorded session (touch.csv + keys.csv) through PG.Engine.
//   replay.exe <sessionDir> [--model model.txt] [--train outDir] [--cv]
// Ground truth for the 2026-11-16 scripted recording is derived from the phase script + obvious palm shape/edge.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PG;

static class Replay
{
    class Row { public double t; public string phase; public int scan, id, tip, conf, x, y, w, h, btn, cx, cy; }
    class FrameRec { public List<Contact> act = new List<Contact>(); public bool block; public int dc; }

    static readonly double[] phaseStart = { 0, 5, 20, 40, 52, 62 };
    static readonly string[] phaseNames = { "ready", "A", "B", "C", "D", "E" };
    static string PhaseAt(double t) { int p = 0; for (int i = 0; i < phaseStart.Length; i++) if (t >= phaseStart[i]) p = i; return phaseNames[p]; }

    static bool ScriptPalm(Contact c, Geom g)
    {
        string ph = PhaseAt(c.t0);
        double sx = c.First.x / g.mmX;
        if (ph == "B" || ph == "D") return true;
        if (sx >= 3550 || sx <= 130) return true;
        if (c.maxH >= 6 || c.maxW >= 5) return true;
        return false;
    }

    // thumbs session: only bottom-middle contacts are labelled (thumb = finger); everything else unlabelled
    static bool? ThumbLabel(Contact c, Geom g)
    {
        double x = c.First.x, y = c.First.y;
        if (y > 55 && x > 25 && x < 95 && !c.fwRejected) return false;
        return null;
    }

    static List<Row> LoadRows(string dir)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "touch.csv")).Skip(1))
        {
            var p = line.Split(',');
            rows.Add(new Row
            {
                t = double.Parse(p[0], CultureInfo.InvariantCulture), phase = p[1], scan = int.Parse(p[2]), id = int.Parse(p[5]), tip = int.Parse(p[6]), conf = int.Parse(p[7]),
                x = int.Parse(p[8]), y = int.Parse(p[9]), w = int.Parse(p[10]), h = int.Parse(p[11]), btn = int.Parse(p[13]), cx = int.Parse(p[14]), cy = int.Parse(p[15])
            });
        }
        return rows;
    }
    static List<double> LoadKeys(string dir)
    {
        var k = new List<double>(); var f = Path.Combine(dir, "keys.csv"); if (!File.Exists(f)) return k;
        foreach (var line in File.ReadLines(f).Skip(1)) k.Add(double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture));
        return k;
    }

    class Result { public List<Contact> contacts = new List<Contact>(); public List<FrameRec> frames = new List<FrameRec>(); public Engine eng; }

    static Result Run(List<Row> rows, List<double> keys, Model m, bool learn)
    {
        var e = new Engine(); e.model = m; e.learn = learn; if (!uncalibrated) e.calib.AssumeReference();
        var res = new Result { eng = e };
        var known = new HashSet<Contact>();
        int ki = 0, i = 0, lcx = rows.Count > 0 ? rows[0].cx : 0, lcy = rows.Count > 0 ? rows[0].cy : 0;
        while (i < rows.Count)
        {
            int j = i; var frame = new List<Touch>();
            bool btn = false;
            while (j < rows.Count && rows[j].scan == rows[i].scan && Math.Abs(rows[j].t - rows[i].t) < 1e-9)
            {
                if (rows[j].btn == 1) btn = true;
                var r = rows[j]; frame.Add(new Touch { id = (uint)r.id, tip = r.tip == 1, conf = r.conf == 1, x = r.x, y = r.y, w = r.w, h = r.h }); j++;
            }
            double t = rows[i].t;
            while (ki < keys.Count && keys[ki] <= t) e.OnKey(keys[ki++]);
            e.OnFrame(t, frame, btn);
            var fr = new FrameRec(); string why;
            fr.block = e.ShouldBlock(out why);
            foreach (var c in e.Active.Values) { fr.act.Add(c); if (known.Add(c)) res.contacts.Add(c); }
            fr.dc = Math.Abs(rows[i].cx - lcx) + Math.Abs(rows[i].cy - lcy); lcx = rows[i].cx; lcy = rows[i].cy;
            res.frames.Add(fr);
            i = j;
        }
        return res;
    }

    static bool uncalibrated = false;
    class Score { public int palmLeaks, fingerBlocked, fingerN, slowFingers; public double fingerLostFrac; }
    static Score Report(string title, Result r, Func<Contact, Geom, bool?> lab)
    {
        var g = r.eng.geom;
        var gt = r.contacts.ToDictionary(c => c, c => lab(c, g));
        int palmN = 0, palmLeakContacts = 0, fingerN = 0, fingerBlockedContacts = 0;
        long leakPx = 0, fingerLostPx = 0, fingerPx = 0;
        var palmLeakFrames = new Dictionary<Contact, int>(); var fingerBlockFrames = new Dictionary<Contact, int>();
        foreach (var f in r.frames)
        {
            bool anyFinger = f.act.Any(c => gt[c] == false);
            bool palmConf = f.act.Any(c => gt[c] == true && c.conf);
            if (!f.block && palmConf && !anyFinger) { leakPx += f.dc; foreach (var c in f.act.Where(c => gt[c] == true && c.conf)) palmLeakFrames[c] = (palmLeakFrames.ContainsKey(c) ? palmLeakFrames[c] : 0) + 1; }
            if (anyFinger) { fingerPx += f.dc; if (f.block) { fingerLostPx += f.dc; foreach (var c in f.act.Where(c => gt[c] == false)) fingerBlockFrames[c] = (fingerBlockFrames.ContainsKey(c) ? fingerBlockFrames[c] : 0) + 1; } }
        }
        var lat = new List<double>();
        foreach (var c in r.contacts)
        {
            if (c.hist.Count < 3 || gt[c] == null) continue;
            if (gt[c] == true) { palmN++; if (palmLeakFrames.ContainsKey(c)) palmLeakContacts++; }
            else { fingerN++; if (fingerBlockFrames.ContainsKey(c)) fingerBlockedContacts++; lat.Add(c.fingerAge < 0 ? 9.99 : c.fingerAge); }
        }
        lat.Sort();
        Console.WriteLine("== " + title);
        Console.WriteLine("  palm contacts: {0}, with any unblocked trusted frame: {1}, cursor leak while only palms down: {2} px", palmN, palmLeakContacts, leakPx);
        Console.WriteLine("  finger contacts: {0}, ever blocked: {1}, finger cursor lost: {2} / {3} px ({4:P1})", fingerN, fingerBlockedContacts, fingerLostPx, fingerPx, fingerPx > 0 ? (double)fingerLostPx / fingerPx : 0);
        if (lat.Count > 0) Console.WriteLine("  finger decision latency: median {0:F0} ms, p90 {1:F0} ms, max {2:F0} ms; still not finger at lift: {3}", lat[lat.Count / 2] * 1000, lat[(int)(lat.Count * 0.9)] * 1000, lat.Last() * 1000, lat.Count(x => x > 9));
        foreach (var kv in palmLeakFrames.OrderByDescending(kv => kv.Value).Take(4)) Console.WriteLine("    leak: palm t0={0:F2} frames={1} why='{2}' start=({3:F0},{4:F0})mm W{5} H{6}", kv.Key.t0, kv.Value, kv.Key.why, kv.Key.First.x, kv.Key.First.y, kv.Key.maxW, kv.Key.maxH);
        foreach (var kv in fingerBlockFrames.OrderByDescending(kv => kv.Value).Take(4)) Console.WriteLine("    blocked finger t0={0:F2} frames={1} why='{2}' start=({3:F0},{4:F0})mm W{5} H{6}", kv.Key.t0, kv.Value, kv.Key.why, kv.Key.First.x, kv.Key.First.y, kv.Key.maxW, kv.Key.maxH);
        return new Score { palmLeaks = palmLeakContacts, fingerBlocked = fingerBlockedContacts, fingerN = fingerN, fingerLostFrac = fingerPx > 0 ? (double)fingerLostPx / fingerPx : 0, slowFingers = fingerBlockFrames.Count(kv => kv.Value > 30) };
    }

    static void Samples(Result r, Func<Contact, Geom, bool?> lab, List<LabeledSample> l, List<int> group, ref int gi)
    {
        foreach (var c in r.contacts)
        {
            var y = lab(c, r.eng.geom); gi++; if (y == null) continue;
            foreach (var cp in c.checkpoints) { l.Add(new LabeledSample { f = cp.Value, y = y == true ? 1 : 0, w = 1.0, src = "seed" }); group.Add(gi); }
        }
    }

    class DS { public string name, dir; public Func<Contact, Geom, bool?> lab; public List<Row> rows; public List<double> keys; }

    static void Main(string[] args)
    {
        Model m = Model.Prior(); string train = null; bool cv = false, assert = false; var sets = new List<DS>();
        for (int a = 0; a < args.Length; a++)
        {
            if (args[a] == "--model") m = Model.Load(args[++a]);
            else if (args[a] == "--train") train = args[++a];
            else if (args[a] == "--cv") cv = true;
            else if (args[a] == "--assert") assert = true;
            else if (args[a] == "--uncalibrated") uncalibrated = true;
            else if (args[a] == "--script") sets.Add(new DS { name = "scripted recording", dir = args[++a], lab = (c, g) => ScriptPalm(c, g) });
            else if (args[a] == "--thumbs") sets.Add(new DS { name = "thumb-click session", dir = args[++a], lab = ThumbLabel });
        }
        foreach (var d in sets) { d.rows = LoadRows(d.dir); d.keys = LoadKeys(d.dir); }
        var samples = new List<LabeledSample>(); var grp = new List<int>(); int gi = 0;
        var failures = new List<string>();
        foreach (var d in sets)
        {
            var r0 = Run(d.rows, d.keys, m, false);
            var sc = Report(d.name + " / model v" + m.version + (m.version == 0 ? " (hand prior)" : ""), r0, d.lab);
            Samples(r0, d.lab, samples, grp, ref gi);
            // regression gates
            if (sc.palmLeaks > 0) failures.Add(d.name + ": " + sc.palmLeaks + " palm contact(s) leaked");
            if (sc.fingerLostFrac > 0.005) failures.Add(d.name + ": finger cursor lost " + sc.fingerLostFrac.ToString("P1"));
            if (sc.slowFingers > 0) failures.Add(d.name + ": " + sc.slowFingers + " finger(s) blocked > 30 frames");
        }
        if (assert)
        {
            if (failures.Count == 0) { Console.WriteLine("ASSERT: all regression gates passed"); }
            else { foreach (var f in failures) Console.WriteLine("ASSERT FAILED: " + f); Environment.Exit(1); }
        }
        Console.WriteLine("checkpoint samples: {0} (palm {1}, finger {2})", samples.Count, samples.Count(s => s.y > 0.5), samples.Count(s => s.y < 0.5));
        if (cv)
        {
            int K = 5; var groups = grp.Distinct().ToList(); var rnd = new Random(7); groups = groups.OrderBy(x => rnd.Next()).ToList();
            var foldOf = new Dictionary<int, int>(); for (int i = 0; i < groups.Count; i++) foldOf[groups[i]] = i % K;
            int fp = 0, nf = 0, tp = 0, np = 0;
            for (int k = 0; k < K; k++)
            {
                var tr = samples.Where((s, i) => foldOf[grp[i]] != k).ToList(); var te = samples.Where((s, i) => foldOf[grp[i]] == k).ToList();
                var fm = Trainer.Fit(tr, Model.Prior(), Model.Prior());
                foreach (var s in te) { double p = fm.P(s.f); if (s.y > 0.5) { np++; if (p >= 0.5) tp++; } else { nf++; if (p >= 0.5) fp++; } }
            }
            Console.WriteLine("5-fold CV by contact (checkpoint level): palm recall {0:P1}, finger false-palm {1:P1}", (double)tp / np, (double)fp / nf);
        }
        if (train != null)
        {
            Directory.CreateDirectory(train);
            File.WriteAllLines(Path.Combine(train, "seed.csv"), samples.Select(Learner.Ser));
            var fit = Trainer.Fit(samples, Model.Prior(), Model.Prior()); fit.version = 1;
            Console.WriteLine("trained seed model: " + Trainer.Eval(samples, fit));
            fit.Save(Path.Combine(train, "model.txt"));
            foreach (var d in sets) Report(d.name + " / trained seed (in-sample)", Run(d.rows, d.keys, fit, false), d.lab);
        }
    }
}
