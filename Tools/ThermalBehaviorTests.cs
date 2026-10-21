using System;
using HeatTileOverlayPrototype;
class ThermalBehaviorTests
{
    static int checks;
    static void Equal(double actual, double expected, string name)
    {
        checks++;
        if (Math.Abs(actual - expected) > 0.001) throw new Exception(name + ": " + actual + " != " + expected);
    }
    static void Main()
    {
        Equal(ThermalProtectionPolicy.Amount(float.NaN), 1, "NaN migration");
        Equal(ThermalProtectionPolicy.Amount(float.PositiveInfinity), 1, "Infinity migration");
        Equal(ThermalProtectionPolicy.Amount(-1), .25, "Minimum");
        Equal(ThermalProtectionPolicy.Amount(999), 4, "Maximum");
        Equal(ThermalProtectionPolicy.Limit(2000, 1, 1.35, 3000, 6000), 2700, "Legacy internal");
        Equal(ThermalProtectionPolicy.Limit(2000, 1, 1.5, 3300, 6600), 3000, "Legacy skin");
        Equal(ThermalProtectionPolicy.Limit(3000, 1, 1.35, 3000, 6000), 3000, "Legacy cap");
        Equal(ThermalProtectionPolicy.Limit(2000, .25f, 1.35, 3000, 6000), 2175, "Minimum internal");
        Equal(ThermalProtectionPolicy.Limit(2000, 4, 1.35, 3000, 6000), 4800, "Maximum internal");
        Equal(ThermalProtectionPolicy.Limit(2000, 4, 1.5, 3300, 6600), 6000, "Maximum skin");
        Equal(ThermalProtectionPolicy.Limit(5000, 4, 1.35, 3000, 6000), 6000, "Raised internal cap");
        Equal(ThermalProtectionPolicy.Limit(5000, 4, 1.5, 3300, 6600), 6600, "Raised skin cap");
        for (int a=1; a<=16; a++)
        {
            Equal(ThermalProtectionPolicy.Limit(9000, a/4f, 1.35, 3000, 6000), 9000, "No downgrade");
            Equal(ThermalProtectionPolicy.Mass(2, a/4f), .1*a/4, "Dry mass");
        }
        Equal(ThermalProtectionPolicy.Mass(float.NaN, 1), 0, "Invalid mass");
        var state = new ThermalLimitOwnership();
        Equal(state.Apply(2400, false, 1, 1.35, 3000, 6000), 2400, "Disabled live variant");
        double v = state.Apply(2000, true, 1, 1.35, 3000, 6000);
        Equal(v, 2700, "Acquire");
        for (int i=0; i<50; i++) v=state.Apply(v, true, 1, 1.35, 3000, 6000);
        Equal(v, 2700, "No compounding");
        v=state.Apply(v, true, 4, 1.35, 3000, 6000);
        Equal(v, 4800, "Amount from baseline");
        Equal(state.Apply(v, false, 1, 1.35, 3000, 6000), 2000, "Restore owned baseline");
        state.Apply(2000, true, 1, 1.35, 3000, 6000);
        Equal(state.Apply(3100, false, 1, 1.35, 3000, 6000), 3100, "Preserve later external write");
        state.Apply(2000, true, 1, 1.35, 3000, 6000);
        v=state.Apply(2500, true, 4, 1.35, 3000, 6000);
        Equal(v, 6000, "External variant becomes baseline");
        Equal(state.Apply(v, false, 1, 1.35, 3000, 6000), 2500, "Restore revised baseline");
        Console.WriteLine("Thermal numerical and ownership behavior: " + checks + " checks passed (not KSP runtime verification).");
    }
}
