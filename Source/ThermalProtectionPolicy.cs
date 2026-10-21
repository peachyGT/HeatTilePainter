using System;

namespace HeatTileOverlayPrototype
{
    // Pure arithmetic and field ownership: exercised without Unity by behavior fixtures.
    internal static class ThermalProtectionPolicy
    {
        internal static float Amount(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 1f
                : Math.Max(0.25f, Math.Min(4f, value));
        }

        internal static double Limit(double baseline, float amount, double multiplier,
            double legacyCap, double maximumCap)
        {
            if (double.IsNaN(baseline) || double.IsInfinity(baseline) || baseline <= 0) return baseline;
            double a = Amount(amount);
            double cap = legacyCap + (maximumCap - legacyCap) * Math.Max(0, a - 1) / 3;
            return Math.Max(baseline, Math.Min(baseline * (1 + (multiplier - 1) * a), cap));
        }

        internal static float Mass(float defaultMass, float amount)
        {
            if (float.IsNaN(defaultMass) || float.IsInfinity(defaultMass) || defaultMass <= 0) return 0;
            return defaultMass * 0.05f * Amount(amount);
        }
    }

    internal sealed class ThermalLimitOwnership
    {
        private bool owned;
        private double baseline;
        private double lastWritten;

        internal double Baseline(double current) { return owned && current == lastWritten ? baseline : current; }

        internal double Apply(double current, bool enabled, float amount, double multiplier,
            double legacyCap, double maximumCap)
        {
            if (!enabled)
            {
                double restored = Baseline(current);
                owned = false;
                return restored;
            }
            if (double.IsNaN(current) || double.IsInfinity(current) || current <= 0) return current;
            baseline = Baseline(current);
            lastWritten = ThermalProtectionPolicy.Limit(baseline, amount, multiplier, legacyCap, maximumCap);
            owned = true;
            return lastWritten;
        }
    }
}
