using System;
using System.Collections.Generic;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Edge conditions of a rectangular Mindlin plate. Hard simple support holds the tangential rotation too (that is what the
    /// Navier series assumes); soft holds w only. Symmetric holds the normal rotation only (w free): a mirror plane, or the edge
    /// of a periodic cell such as the panel between pillars.
    /// </summary>
    public enum EdgeBc { Free, SimpleHard, SimpleSoft, Clamped, Symmetric }

    /// <summary>Uniform pressure on a rectangle (mm, MPa); the part outside the plate is ignored. Positive p pushes toward +w.</summary>
    public sealed class PressurePatch
    {
        public double X1, X2, Y1, Y2, P;
        public PressurePatch() { }
        public PressurePatch(double x1, double x2, double y1, double y2, double p) { X1 = x1; X2 = x2; Y1 = y1; Y2 = y2; P = p; }
    }

    /// <summary>Rigid line support (w = 0) along the grid line x = Position (Axis 'x') or y = Position (Axis 'y'), between From and To.</summary>
    public sealed class LineSupport
    {
        public char Axis = 'x';
        public double Position;
        public double From = double.NegativeInfinity, To = double.PositiveInfinity;
        public LineSupport() { }
        public LineSupport(char axis, double position) { Axis = axis; Position = position; }
    }

    /// <summary>Linear spring to ground on w at a point (N/mm), e.g. a support pillar k = E A / L. Spread to the element's nodes by its shape functions.</summary>
    public sealed class PointSpring
    {
        public double X, Y, K;
        public PointSpring() { }
        public PointSpring(double x, double y, double k) { X = x; Y = y; K = k; }
    }

    /// <summary>A point force on w (N), spread by the shape functions like a spring.</summary>
    public sealed class PointLoad
    {
        public double X, Y, P;
        public PointLoad() { }
        public PointLoad(double x, double y, double p) { X = x; Y = y; P = p; }
    }

    /// <summary>
    /// Winkler foundation k (N/mm^3) on a rectangle, e.g. platen contact. It resists +w (the plate pressing into it).
    /// Unilateral: elements whose mean w is negative (lifting off) are dropped and the plate re-solved (up to MaxPasses).
    /// </summary>
    public sealed class WinklerRegion
    {
        public double X1, X2, Y1, Y2, K;
        public bool Unilateral;
        public WinklerRegion() { }
        public WinklerRegion(double x1, double x2, double y1, double y2, double k, bool unilateral = false)
        { X1 = x1; X2 = x2; Y1 = y1; Y2 = y2; K = k; Unilateral = unilateral; }
    }

    /// <summary>
    /// Region where every node is held at w = 0 (a stop pad on a rigid platen). Clamped also holds the rotations: a rigid head
    /// the plate cannot slide on (a pillar under molding pressure, where friction holds the plate's face), and the normal moment
    /// on each face of the region is then recovered from the nodal rotation reactions, as on clamped edges.
    /// </summary>
    public sealed class FixedRegion
    {
        public double X1, X2, Y1, Y2;
        public bool Clamped;
        public FixedRegion() { }
        public FixedRegion(double x1, double x2, double y1, double y2, bool clamped = false) { X1 = x1; X2 = x2; Y1 = y1; Y2 = y2; Clamped = clamped; }
    }

    /// <summary>
    /// A rectangular Mindlin plate, A along x and B along y (mm), thickness T, isotropic E (MPa) and Nu. The grid is
    /// rectangular: near-uniform, with grid lines forced through every patch, line support, fixed region and output edge
    /// listed in XLines/YLines (and through the patch and region edges automatically when SnapToFeatures is on).
    /// </summary>
    public sealed class PlateModel
    {
        public double A, B, T;
        public double E = 1.0, Nu = 0.3;
        /// <summary>Shear correction factor kappa_s.</summary>
        public double ShearFactor = 5.0 / 6.0;
        public EdgeBc X0 = EdgeBc.SimpleHard, XA = EdgeBc.SimpleHard, Y0 = EdgeBc.SimpleHard, YB = EdgeBc.SimpleHard;

        /// <summary>Elements along x and y. 0 = automatic: ElementsOnShortSide on the shorter side, the same size on the other.</summary>
        public int Nx, Ny;
        public int ElementsOnShortSide = 24;
        /// <summary>Target element size (mm); overrides ElementsOnShortSide when positive (symmetry-reduced models keep the full model's mesh).</summary>
        public double ElementSize;
        /// <summary>Extra grid lines; patch, region and support edges are added when SnapToFeatures is true.</summary>
        public List<double> XLines = new List<double>(), YLines = new List<double>();
        public bool SnapToFeatures = true;

        public List<PressurePatch> Pressures = new List<PressurePatch>();
        /// <summary>
        /// Optional pressure field (x, y) -> p, e.g. a cavity outline mask; added to the patches. Sampled at PressureFieldSamples^2
        /// points per element, so an outline off the grid lines is followed to within 1/PressureFieldSamples of an element.
        /// </summary>
        public Func<double, double, double> PressureField;
        public int PressureFieldSamples = 8;
        /// <summary>Largest band storage the solver may allocate (MB); a bigger model throws before allocating (it runs inside SolidWorks).</summary>
        public double MaxMemoryMB = 400;
        public List<LineSupport> Lines = new List<LineSupport>();
        public List<PointSpring> Springs = new List<PointSpring>();
        public List<PointLoad> PointLoads = new List<PointLoad>();
        public List<WinklerRegion> Foundations = new List<WinklerRegion>();
        public List<FixedRegion> Fixed = new List<FixedRegion>();
        /// <summary>Passes of the unilateral foundation loop (3-6 is usual).</summary>
        public int MaxPasses = 8;

        public double D => E * T * T * T / (12 * (1 - Nu * Nu));
        public double G => E / (2 * (1 + Nu));

        public static PlateModel Rect(double a, double b, double t, double e, double nu, EdgeBc x0, EdgeBc xa, EdgeBc y0, EdgeBc yb)
            => new PlateModel { A = a, B = b, T = t, E = e, Nu = nu, X0 = x0, XA = xa, Y0 = y0, YB = yb };

        /// <summary>Uniform pressure over the whole plate.</summary>
        public PlateModel Uniform(double p) { Pressures.Add(new PressurePatch(0, A, 0, B, p)); return this; }

        public PlateModel Mesh(int nx, int ny) { Nx = nx; Ny = ny; return this; }

        internal void Validate()
        {
            if (!(A > 0) || !(B > 0) || !(T > 0)) throw new ArgumentException("Plate A, B and T must be positive.");
            if (!(E > 0) || Nu <= -1 || Nu >= 0.5) throw new ArgumentException("Plate E must be positive and -1 < nu < 0.5.");
            // features must lie on the plate: a reversed patch would load nothing, and a support off the plate would snap to its
            // edge or vanish without a word
            double tol = 1e-9 * Math.Max(A, B);
            bool InX(double x) => x >= -tol && x <= A + tol;
            bool InY(double y) => y >= -tol && y <= B + tol;
            foreach (var p in Pressures)
            {
                if (!(p.X1 < p.X2) || !(p.Y1 < p.Y2)) throw new ArgumentException($"Pressure patch ({p.X1}, {p.Y1})-({p.X2}, {p.Y2}) is empty or reversed (X1 < X2 and Y1 < Y2).");
                if (p.X2 <= 0 || p.X1 >= A || p.Y2 <= 0 || p.Y1 >= B) throw new ArgumentException($"Pressure patch ({p.X1}, {p.Y1})-({p.X2}, {p.Y2}) lies off the {A} x {B} plate.");
            }
            foreach (var r in Foundations)
            {
                if (!(r.X1 < r.X2) || !(r.Y1 < r.Y2)) throw new ArgumentException($"Foundation ({r.X1}, {r.Y1})-({r.X2}, {r.Y2}) is empty or reversed.");
                if (r.X2 <= 0 || r.X1 >= A || r.Y2 <= 0 || r.Y1 >= B) throw new ArgumentException($"Foundation ({r.X1}, {r.Y1})-({r.X2}, {r.Y2}) lies off the plate.");
                if (!(r.K >= 0)) throw new ArgumentException("Foundation modulus must not be negative.");
            }
            foreach (var r in Fixed)
                if (!(r.X1 <= r.X2) || !(r.Y1 <= r.Y2) || !InX(r.X1) || !InX(r.X2) || !InY(r.Y1) || !InY(r.Y2))
                    throw new ArgumentException($"Fixed region ({r.X1}, {r.Y1})-({r.X2}, {r.Y2}) is reversed or not on the {A} x {B} plate.");
            foreach (var l in Lines)
            {
                if (l.Axis != 'x' && l.Axis != 'y') throw new ArgumentException("Line support axis must be 'x' or 'y'.");
                if (!(l.Axis == 'x' ? InX(l.Position) : InY(l.Position))) throw new ArgumentException($"Line support {l.Axis} = {l.Position} is off the plate.");
                if (!(l.From <= l.To)) throw new ArgumentException("Line support From must not exceed To.");
            }
            foreach (var s in Springs)
                if (!InX(s.X) || !InY(s.Y) || !(s.K >= 0)) throw new ArgumentException($"Spring at ({s.X}, {s.Y}) is off the plate or negative.");
            foreach (var p in PointLoads)
                if (!InX(p.X) || !InY(p.Y)) throw new ArgumentException($"Point load at ({p.X}, {p.Y}) is off the plate.");
        }
    }
}
