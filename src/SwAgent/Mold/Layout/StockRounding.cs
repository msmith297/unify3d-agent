using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    public sealed class StockChoice
    {
        /// <summary>Finished thickness (mm) and the stock bought for it (mm), with the display value in the system's unit.</summary>
        public double Finished, Stock;
        public bool FromList;
        public string Note;
    }

    /// <summary>
    /// S75 rounding to stock. The finished thickness is the required one rounded up to the shop's step (0.125 in; 1 mm in
    /// metric documents); the stock is the smallest listed thickness that leaves it after skimming both faces (MIC-6 and
    /// other ground cast plate: no skim, sold to finished size, so the finished thickness is a list size). Grades without
    /// a list fall back to the step. L x W round up to the shop's 0.5 in.
    /// </summary>
    public static class StockRounding
    {
        public static StockChoice Thickness(double required, JObject grade, MoldData data, bool imperial)
        {
            var c = new StockChoice();
            var step = imperial ? data.ShopMm("plates.roundT", 3.175) : data.ShopMm("plates.roundTMetric", 1.0);
            var skim = (double?)grade?["skimPerFace_mm"] ?? data.ShopMm("plates.skimPerFace", 1.5);
            var key = (string)grade?["stockKey"];
            var list = key == null ? null : data.Raw("mold-materials.json")?["stock_thickness"]?[key]?[imperial ? "inch" : "metric_mm"] as JArray;
            var sizes = list?.Select(x => Si.Num(x)).Where(x => x != null).Select(x => imperial ? x.Value * Si.InToMm : x.Value).OrderBy(x => x).ToList();
            if (skim <= 0 && sizes != null && sizes.Count > 0)
            {
                // sold to finished thickness
                var pick = sizes.FirstOrDefault(s => s >= required - 1e-9);
                if (pick > 0) { c.Finished = c.Stock = pick; c.FromList = true; return c; }
            }
            c.Finished = Math.Ceiling(required / step - 1e-9) * step;
            if (sizes != null && sizes.Count > 0)
            {
                var stock = sizes.FirstOrDefault(s => s - 2 * skim >= c.Finished - 1e-9);
                if (stock > 0) { c.Stock = stock; c.FromList = true; return c; }
                c.Note = "thicker than the largest listed stock: block or forging";
            }
            c.Stock = c.Finished + 2 * skim;
            return c;
        }

        /// <summary>L x W rounded up to the shop step (0.5 in; 10 mm in metric documents).</summary>
        public static double Planform(double required, MoldData data, bool imperial)
        {
            var step = imperial ? data.ShopMm("plates.roundLW", 12.7) : 10.0;
            return Math.Ceiling(required / step - 1e-9) * step;
        }
    }
}
