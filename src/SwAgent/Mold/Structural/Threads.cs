using System;
using System.Linq;

namespace SwAgent.Mold
{
    public sealed class ThreadResult
    {
        /// <summary>Required engagement L_e/d for a direct tapped hole (S54), and the choice.</summary>
        public double LeOverD;
        public bool Insert;
        public string InsertKind, Reason;
        /// <summary>Insert length in d (helical) and its re-checked need at the larger shear diameter.</summary>
        public double InsertLengthD, InsertNeedD;
        /// <summary>Engagement used (mm), tapped depth incl. 0.5 d relief (mm), stripping stress and allowable (S56).</summary>
        public double Le, TapDepth, StripStress, StripAllow;
    }

    /// <summary>
    /// S54-S56 threads in the parent material.
    /// (S54) L_e/d >= max(1.0, 0.5 S_u,bolt / S_u,parent,T).
    /// (S55) a steel insert when L_e/d > 2.5, the screw comes out routinely, or N >= 1e4 with a load every cycle. Helical
    /// coil re-checked at its outer shear diameter (~1.2 d): length = the smallest stock length >= need/1.2 and >= 2 d in
    /// aluminium; key-locking (~1.75 d) above 3 d.
    /// (S56) stripping of a direct tapped hole tau = F_b,max/(pi d_minor L_e 0.75) <= 0.58 S_y,T/2 - deliberately
    /// conservative (critique H4: the aluminium internal thread really shears at about pi d L_e 0.88, 1.45 x this area for
    /// 1/2-13); do not "fix" it to the larger area.
    /// </summary>
    public static class Threads
    {
        public static ThreadResult Engagement(BoltSpec b, GradeAtT parent, Cfg cfg, bool removedRoutinely = false, bool cyclicLoad = false, double cycles = 0)
        {
            var r = new ThreadResult();
            r.LeOverD = Math.Max(1.0, 0.5 * b.Su / parent.Su);
            var limit = cfg.D("bolts.insertAboveLeD", 2.5);
            if (r.LeOverD > limit) { r.Insert = true; r.Reason = "L_e/d " + Si.F(r.LeOverD) + " > " + Si.F(limit); }
            else if (removedRoutinely) { r.Insert = true; r.Reason = "screw removed routinely"; }
            else if (cyclicLoad && cycles >= 1e4) { r.Insert = true; r.Reason = "loaded every cycle, N >= 1e4"; }
            if (r.Insert && parent.IsAluminium)
            {
                var fh = cfg.D("threads.insertShearFactorHelical", 1.2);
                r.InsertNeedD = r.LeOverD / fh;
                var lengths = cfg.Arr("threads.helicalLengthsD");
                if (lengths.Length == 0) lengths = new[] { 1.5, 2.0, 2.5, 3.0 };
                var minD = cfg.D("threads.minHelicalInAluminiumD", 2.0);
                var pick = lengths.Where(x => x >= r.InsertNeedD - 1e-9 && x >= minD - 1e-9).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(pick))
                {
                    r.InsertKind = "helical coil " + Si.F(pick) + " d";
                    r.InsertLengthD = pick;
                }
                else
                {
                    var fk = cfg.D("threads.insertShearFactorKeyLocking", 1.75);
                    r.InsertNeedD = r.LeOverD / fk;
                    r.InsertLengthD = Math.Max(1.5, Math.Ceiling(r.InsertNeedD * 4) / 4);
                    r.InsertKind = "heavy key-locking insert, " + Si.F(r.InsertLengthD) + " d";
                }
                r.Le = r.InsertLengthD * b.D;
            }
            else if (r.Insert)
            {
                r.InsertKind = "helical coil 1.5 d";
                r.InsertLengthD = 1.5;
                r.Le = Math.Max(r.LeOverD, 1.5) * b.D;
            }
            else r.Le = r.LeOverD * b.D;
            r.TapDepth = r.Le + cfg.D("threads.tappedReliefD", 0.5) * b.D;
            return r;
        }

        /// <summary>
        /// S54 + S55 + S56 together for a known maximum bolt load: the engagement that S54 needs, lengthened until the
        /// conservative stripping check S56 passes; above 2.5 d (or when S54/S55 already ask for one) a steel insert, its
        /// length chosen so the parent material passes S56 at the insert's larger shear diameter (1.2 d helical, 1.75 d
        /// key-locking).
        /// </summary>
        public static ThreadResult ForLoad(BoltSpec b, GradeAtT parent, double fBoltMax, Cfg cfg, bool removedRoutinely = false, bool cyclicLoad = false, double cycles = 0)
        {
            var r = Engagement(b, parent, cfg, removedRoutinely, cyclicLoad, cycles);
            var fill = cfg.D("threads.stripThreadFill", 0.75);
            var allow = 0.58 * parent.Sy / cfg.D("sfStatic", 2.0);
            var leStrip = fBoltMax / (Math.PI * b.MinorDia * fill * allow);
            r.StripAllow = allow;
            if (!r.Insert)
            {
                if (leStrip <= cfg.D("bolts.insertAboveLeD", 2.5) * b.D)
                {
                    r.Le = Math.Max(r.Le, leStrip);
                    r.TapDepth = r.Le + cfg.D("threads.tappedReliefD", 0.5) * b.D;
                    r.StripStress = fBoltMax / (Math.PI * b.MinorDia * r.Le * fill);
                    return r;
                }
                r.Insert = true;
                r.Reason = "S56: stripping needs L_e " + Si.F(leStrip / b.D) + " d > 2.5 d";
            }
            if (!parent.IsAluminium) { r.InsertKind = r.InsertKind ?? "helical coil 1.5 d"; return r; }
            // the parent shears at the insert's outer diameter
            var fh = cfg.D("threads.insertShearFactorHelical", 1.2);
            var lengths = cfg.Arr("threads.helicalLengthsD");
            if (lengths.Length == 0) lengths = new[] { 1.5, 2.0, 2.5, 3.0 };
            var need = Math.Max(r.LeOverD / fh, Math.Max(cfg.D("threads.minHelicalInAluminiumD", 2.0), fBoltMax / (Math.PI * fh * b.D * fill * allow) / b.D));
            var pick = lengths.Where(x => x >= need - 1e-9).DefaultIfEmpty(double.NaN).Min();
            if (!double.IsNaN(pick))
            {
                r.InsertKind = "helical coil " + Si.F(pick) + " d";
                r.InsertLengthD = pick;
                r.InsertNeedD = need;
                r.StripStress = fBoltMax / (Math.PI * fh * b.D * pick * b.D * fill);
            }
            else
            {
                var fk = cfg.D("threads.insertShearFactorKeyLocking", 1.75);
                var needK = Math.Max(1.5, fBoltMax / (Math.PI * fk * b.D * fill * allow) / b.D);
                r.InsertLengthD = Math.Ceiling(Math.Max(needK, r.LeOverD / fk) * 4) / 4;
                r.InsertNeedD = needK;
                r.InsertKind = "heavy key-locking insert, " + Si.F(r.InsertLengthD) + " d";
                r.StripStress = fBoltMax / (Math.PI * fk * b.D * r.InsertLengthD * b.D * fill);
            }
            r.Le = r.InsertLengthD * b.D;
            r.TapDepth = r.Le + cfg.D("threads.tappedReliefD", 0.5) * b.D;
            return r;
        }

        /// <summary>(S56) stripping stress of a direct tapped hole at the bolt's maximum load (N), and its allowable.</summary>
        public static (double Tau, double Allow) Stripping(BoltSpec b, double le, double fBoltMax, GradeAtT parent, Cfg cfg)
        {
            var fill = cfg.D("threads.stripThreadFill", 0.75);
            var tau = fBoltMax / (Math.PI * b.MinorDia * le * fill);
            return (tau, 0.58 * parent.Sy / cfg.D("sfStatic", 2.0));
        }
    }
}
