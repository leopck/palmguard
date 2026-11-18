// Gestures: two-finger scrolling with Mac-style axis lock + momentum, and two-finger press = right click.
// Pure logic (no Win32): fed every frame after Engine.OnFrame; the host sends the wheel units it returns.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PG
{
    public class Gestures
    {
        public enum Mode { Idle, Deciding, Scroll, Pinch, Ignore }
        public enum Axis { None, Vertical, Horizontal, Free }

        // tuning
        public double GainPerMm = 14;          // wheel units (120 = one notch) per mm of finger travel, slow speed
        public double AccelFrom = 30, AccelSpan = 120, AccelMax = 1.5; // mm/s; extra gain up to +150% for fast swipes
        public bool Natural = true;            // content follows fingers (Windows default "down motion scrolls up")
        public bool Momentum = true;
        public double DecideMm = 1.5;          // centroid travel before the gesture is classified
        public double LockRatio = 1.8;         // dominant/minor ratio for an axis lock at the start
        public double BreakMm = 4.0, BreakRatio = 1.5, BreakTau = 0.12; // sustained cross-axis motion that breaks a lock
        public double MomentumTau = 0.45, MomentumMin = 60, MomentumStop = 8; // mm/s
        public double ClickMaxDistMm = 45;

        // state (read by the dashboard)
        public Mode mode = Mode.Idle; public Axis axis = Axis.None;
        public bool momentumActive; public int scrollEvents, lockBreaks, rightClicks;

        Contact ca, cb; double a0x, a0y, b0x, b0y, c0x, c0y, d0, lastCx, lastCy, lastT, lastMoveT;
        double lastBreakT = -9, vx, vy, wx, wy, accX, accY, mvx, mvy, mGain, mLastT; int lastCount;
        public double scrollUntil = -1;        // host suppresses touchpad cursor motion until this time

        double Gain(double speed) { return GainPerMm * (1 + Math.Max(0, Math.Min(AccelMax, (speed - AccelFrom) / AccelSpan))); }

        // finger-like contacts: trusted, not palm, and not a resting thumb (a resting thumb never takes part, as on a Mac)
        static List<Contact> Fingers(Engine e)
        {
            var l = new List<Contact>();
            foreach (var c in e.Active.Values) if (c.conf && c.cls != Cls.Palm && !c.hardPalm && c.hist.Count > 0 && !ThumbLike(c, e)) l.Add(c);
            return l;
        }

        // returns wheel units to send this frame (vertical: + = scroll up; horizontal: + = scroll right)
        public void Frame(double t, Engine e, out int wheelY, out int wheelX)
        {
            wheelY = wheelX = 0;
            var f = Fingers(e);
            if (e.Active.Count > lastCount && momentumActive) momentumActive = false; // new touch catches the page
            lastCount = e.Active.Count;

            bool same = f.Count == 2 && ((f[0] == ca && f[1] == cb) || (f[0] == cb && f[1] == ca)); // object identity: firmware recycles ids
            if (!same)
            {
                if (mode == Mode.Scroll) End(t);
                mode = Mode.Idle; axis = Axis.None;
                if (f.Count != 2) { ca = cb = null; return; }
                ca = f[0]; cb = f[1]; momentumActive = false;
                var A0 = f[0].Last; var B0 = f[1].Last;
                a0x = A0.x; a0y = A0.y; b0x = B0.x; b0y = B0.y; c0x = (a0x + b0x) / 2; c0y = (a0y + b0y) / 2; d0 = Dist(a0x, a0y, b0x, b0y);
                lastCx = c0x; lastCy = c0y; lastT = lastMoveT = t; vx = vy = wx = wy = accX = accY = 0;
                mode = Mode.Deciding; return;
            }
            var A = ca.Last; var B = cb.Last;
            double cx = (A.x + B.x) / 2, cy = (A.y + B.y) / 2;

            if (mode == Mode.Deciding)
            {
                double d1x = A.x - a0x, d1y = A.y - a0y, d2x = B.x - b0x, d2y = B.y - b0y;
                double m1 = Math.Sqrt(d1x * d1x + d1y * d1y), m2 = Math.Sqrt(d2x * d2x + d2y * d2y);
                double dispX = cx - c0x, dispY = cy - c0y, disp = Math.Sqrt(dispX * dispX + dispY * dispY);
                double dD = Math.Abs(Dist(A.x, A.y, B.x, B.y) - d0);
                if (dD > 5 && dD > disp) { mode = Mode.Pinch; return; }                              // fingers spreading: Windows zoom
                if (Math.Max(m1, m2) > 4 && Math.Min(m1, m2) < 0.3 * Math.Max(m1, m2)) { mode = Mode.Ignore; return; } // resting thumb + pointer
                // both fingers must travel together; a pointer moving next to a resting finger is not a scroll
                if (disp < DecideMm || Math.Min(m1, m2) < 0.5 * Math.Max(m1, m2) || d1x * d2x + d1y * d2y < 0.5 * m1 * m2) return;
                mode = Mode.Scroll;
                double ax = Math.Abs(dispX), ay = Math.Abs(dispY);
                axis = ay >= LockRatio * ax ? Axis.Vertical : ax >= LockRatio * ay ? Axis.Horizontal : Axis.Free;
                lastCx = c0x; lastCy = c0y; lastT = t - 0.02; // the deciding travel is scrolled too, nothing lost
            }
            if (mode != Mode.Scroll) return;

            double dx = cx - lastCx, dy = cy - lastCy, dt = Math.Max(1e-3, t - lastT);
            lastCx = cx; lastCy = cy; lastT = t;
            double al = 1 - Math.Exp(-dt / 0.04);
            vx += al * (dx / dt - vx); vy += al * (dy / dt - vy);
            if (Math.Abs(dx) + Math.Abs(dy) > 0.05) lastMoveT = t;

            // sticky lock: break it only on sustained, clearly cross-axis motion
            double k = Math.Exp(-dt / BreakTau); wx = wx * k + dx; wy = wy * k + dy;
            Axis to = Axis.None;
            if (axis == Axis.Vertical && Math.Abs(wx) > BreakMm && Math.Abs(wx) > BreakRatio * Math.Abs(wy)) to = Axis.Horizontal;
            else if (axis == Axis.Horizontal && Math.Abs(wy) > BreakMm && Math.Abs(wy) > BreakRatio * Math.Abs(wx)) to = Axis.Vertical;
            if (to != Axis.None)
            {
                // a second break soon after the first means the fingers are wandering (circles, diagonals): go free
                axis = t - lastBreakT < 0.5 ? Axis.Free : to; lastBreakT = t; wx = wy = 0; lockBreaks++;
            }

            if (axis == Axis.Vertical) dx = 0; else if (axis == Axis.Horizontal) dy = 0;
            double g = Gain(Math.Sqrt(vx * vx + vy * vy));
            Emit(dx * g, dy * g, out wheelY, out wheelX);
            scrollUntil = t + 0.15;
        }

        void End(double t)
        {
            double px = axis == Axis.Vertical ? 0 : vx, py = axis == Axis.Horizontal ? 0 : vy, sp = Math.Sqrt(px * px + py * py);
            if (Momentum && sp > MomentumMin && t - lastMoveT < 0.05)
            { momentumActive = true; mvx = px; mvy = py; mGain = Gain(sp); mLastT = t; }
            scrollUntil = t + 0.15;
        }

        // momentum step; call often (~120 Hz) from a timer
        public void Tick(double t, out int wheelY, out int wheelX)
        {
            wheelY = wheelX = 0;
            if (!momentumActive) return;
            double dt = t - mLastT; if (dt <= 0) return; mLastT = t;
            double k = Math.Exp(-dt / MomentumTau);
            // integral of v*exp(-s/tau) over dt
            double dx = mvx * MomentumTau * (1 - k), dy = mvy * MomentumTau * (1 - k);
            mvx *= k; mvy *= k;
            if (Math.Sqrt(mvx * mvx + mvy * mvy) < MomentumStop) momentumActive = false;
            Emit(dx * mGain, dy * mGain, out wheelY, out wheelX);
        }
        public void StopMomentum() { momentumActive = false; }

        void Emit(double ux, double uy, out int wheelY, out int wheelX)
        {
            double s = Natural ? 1 : -1;
            accY += s * uy; accX -= s * ux;   // finger down -> content down (scroll up); finger right -> content right (scroll left)
            wheelY = (int)accY; accY -= wheelY; wheelX = (int)accX; accX -= wheelX;
            if (wheelY != 0 || wheelX != 0) scrollEvents++;
        }

        // A physical press while exactly two finger-like contacts are down, close together, neither a resting thumb.
        public bool IsTwoFingerPress(Engine e)
        {
            var f = Fingers(e);
            if (f.Count != 2) return false;
            foreach (var c in f) if (ThumbLike(c, e)) return false;
            return Dist(f[0].Last.x, f[0].Last.y, f[1].Last.x, f[1].Last.y) <= ClickMaxDistMm;
        }
        // side of a thumb: wide and short. A pressing fingertip flattens but stays roughly round.
        // or anything clearly wider than a fingertip resting in the thumb zone.
        public static bool ThumbLike(Contact c, Engine e)
        {
            if (c.maxW >= 1.25 * e.calib.fingerW && c.maxH <= 0.8 * c.maxW) return true;
            return c.maxW >= e.calib.fingerW && c.First.y >= e.cfg.ThumbZone * e.geom.Hmm && c.Net < 3;
        }

        static double Dist(double ax, double ay, double bx, double by) { return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)); }
    }
}
