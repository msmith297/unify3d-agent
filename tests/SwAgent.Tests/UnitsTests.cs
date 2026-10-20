using System;
using SolidWorks.Interop.swconst;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>Units of tool inputs: exact factors, the spec/SI/display roles, formatting for the agent, and IPS detection.</summary>
    public class UnitsTests
    {
        private static void Near(double expected, double actual, double relative = 1e-12) =>
            Assert.True(Math.Abs(expected - actual) <= relative * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

        [Fact]
        public void Factors_are_exact()
        {
            Near(6894.757293168, Units.Find("pressure", "psi").ToSi);
            Near(1e5, Units.Find("pressure", "bar").ToSi);
            Near(98066.5, Units.Find("pressure", "kgf/cm2").ToSi);
            Near(0.45359237, Units.Find("mass", "lb").ToSi);
            Near(4.4482216152605, Units.Find("force", "lbf").ToSi);
            Near(8896.443230521, Units.Find("tonnage", "US ton").ToSi);
            Near(9806.65, Units.Find("tonnage", "t").ToSi);
            Near(0.001, Units.Find("viscosity", "cP").ToSi);
            Near(60, Units.Find("time", "min").ToSi);
            Near(Math.PI / 180, Units.Find("angle", "deg").ToSi);
        }

        [Fact]
        public void Fahrenheit_handles_the_offset()
        {
            var f = Units.Find("temperature", "degF");
            Near(100, f.ToSiValue(212));
            Near(0, f.ToSiValue(32));
            Near(-40, f.ToSiValue(-40));
            Near(248, f.FromSi(120));
            Near(302, f.FromSi(150));
            Near(-273.15, Units.Find("temperature", "K").ToSiValue(0));
        }

        [Fact]
        public void Symbols_and_aliases_resolve_within_their_quantity()
        {
            Assert.Same(Units.Find("pressure", "kgf/cm2"), Units.Find("pressure", "kgf/cm²"));
            Assert.Same(Units.Find("viscosity", "Pa*s"), Units.Find("viscosity", "Pa·s"));
            Assert.Same(Units.Find("temperature", "°F"), Units.Find("temperature", "degF"));
            Assert.Same(Units.Find("tonnage", "US ton"), Units.Find("tonnage", "tons"));
            Assert.Equal("tonnage", Units.Find("tonnage", "kN").Quantity);
            Assert.Null(Units.Find("pressure", "kg"));
            Assert.Equal("pressure", Units.FindAny("psi").Quantity);
            Assert.Equal("temperature", Units.FindAny("degC").Quantity);
            Assert.Null(Units.FindAny("furlong"));
        }

        [Fact]
        public void Spec_display_and_si_roles()
        {
            // Spec units: what Default/Min/Max and data files use.
            Near(13.79e6, Units.SpecToSi("pressure", 13.79));
            Near(100, Units.SpecToSi("temperature", 100));
            Near(444800, Units.SpecToSi("tonnage", 444.8));
            Near(50000, Units.SpecToSi("force", 50));
            Near(1.2, Units.SpecToSi("viscosity", 1200));
            Near(360, Units.SpecToSi("time", 6));
            Near(0.0125, Units.SpecToSi(null, 12.5)); // no unit = length, as always
            Near(1.5, Units.SpecToSi("percent", 1.5));
            Near(13.79, Units.SiToSpec("pressure", 13.79e6));

            Assert.Equal("psi", Units.For("pressure", true).Ascii);
            Assert.Equal("MPa", Units.For("pressure", false).Ascii);
            Assert.Equal("degF", Units.For("temperature", true).Ascii);
            Assert.Equal("lb", Units.For("mass", true).Ascii);
            Assert.Equal("kg", Units.For("mass", false).Ascii);
            Assert.Equal("US ton", Units.For("tonnage", true).Ascii);
            Assert.Equal("t", Units.For("tonnage", false).Ascii);
            Assert.Equal("cP", Units.For("viscosity", true).Ascii);
            Assert.Equal("min", Units.For("time", true).Ascii);
            Assert.Equal("none", Units.Quantity("furlongs")); // unknown units stay plain numbers
            Assert.True(Units.IsConverted("pressure"));
            Assert.False(Units.IsConverted("length"));
            Assert.False(Units.IsConverted("percent"));
        }

        [Fact]
        public void Describe_gives_the_value_as_seen_and_the_reference_figure()
        {
            var psi = Units.Find("pressure", "psi");
            Assert.Equal("2000 psi (13.79 MPa)", Units.Describe("pressure", psi.ToSiValue(2000), psi));
            Assert.Equal("150 psi (1.034 MPa)", Units.Describe("pressure", psi.ToSiValue(150), psi));
            Assert.Equal("13.79 MPa", Units.Describe("pressure", 13.79e6, Units.For("pressure", false)));
            Assert.Equal("212 degF (100 degC)", Units.Describe("temperature", 100, Units.For("temperature", true)));
            Assert.Equal("100 degC", Units.Describe("temperature", 100, Units.For("temperature", false)));
            Assert.Equal("1200 cP (1.2 Pa*s)", Units.Describe("viscosity", 1.2, Units.For("viscosity", false)));
            Assert.Equal("50 US ton (444.8 kN)", Units.Describe("tonnage", Units.Find("tonnage", "US ton").ToSiValue(50), Units.Find("tonnage", "US ton")));
            Assert.Equal("51 lb (23.13 kg)", Units.Describe("mass", Units.Find("mass", "lb").ToSiValue(51), Units.For("mass", true)));
            Assert.Equal("6 min", Units.Describe("time", 360, Units.For("time", false)));
            Assert.Equal("150 bar (15 MPa)", Units.Describe("pressure", 15e6, Units.Find("pressure", "bar")));
        }

        [Fact]
        public void Format_never_uses_exponents_and_keeps_integers()
        {
            Assert.Equal("0", Units.Format(0));
            Assert.Equal("444822", Units.Format(444822.16, 4));
            Assert.Equal("13.79", Units.Format(13.789514, 4));
            Assert.Equal("0.001235", Units.Format(0.00123456, 4));
            Assert.Equal("-40", Units.Format(-40.000001, 6));
            Assert.Equal("1200", Units.Format(1200.0000001, 6));
            Assert.Equal(2000, Units.Nice(2000.07, 4));
            Assert.Equal(50, Units.Nice(49.99752, 4));
            Assert.Equal(445000, Units.Nice(444822, 3));
        }

        [Fact]
        public void Show_uses_the_page_symbol_or_ascii()
        {
            Assert.Equal("248 °F", Units.Show(120, Units.For("temperature", true), 4));
            Assert.Equal("248 degF", Units.Show(120, Units.For("temperature", true), 4, ascii: true));
            Assert.Equal("1.5 %", Units.Show(1.5, Units.For("percent", false)));
            Assert.Equal("1.1", Units.Show(1.1, Units.For("none", false)));
        }

        [Fact]
        public void Legacy_payload_text_is_unchanged()
        {
            // The text existing tools have always sent to the agent.
            Assert.Equal("12 mm (0.012 m)", Units.Legacy("length", 0.012));
            Assert.Equal("12 mm (0.012 m)", Units.Legacy(null, 0.012));
            Assert.Equal("5 deg", Units.Legacy("angle", 5 * Math.PI / 180));
            Assert.Equal("3.25", Units.Legacy("none", 3.25));
            Assert.Equal("3.25", Units.Legacy("furlongs", 3.25));
            Assert.Equal("1.5", Units.Legacy("percent", 1.5)); // the page adds " %" only for fields using the newer options
        }

        [Fact]
        public void Imperial_detection_follows_the_document_units()
        {
            int Sys(swUnitSystem_e s) => (int)s;
            int Len(swLengthUnit_e l) => (int)l;
            var kg = (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Kilograms;
            var lb = (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Pounds;
            Assert.True(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_IPS), Len(swLengthUnit_e.swMM), kg));
            Assert.False(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_MMGS), Len(swLengthUnit_e.swINCHES), lb));
            Assert.False(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_MKS), Len(swLengthUnit_e.swMETER), kg));
            Assert.False(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_CGS), Len(swLengthUnit_e.swCM), kg));
            Assert.True(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_Custom), Len(swLengthUnit_e.swINCHES), kg));
            Assert.True(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_Custom), Len(swLengthUnit_e.swFEETINCHES), kg));
            Assert.True(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_Custom), Len(swLengthUnit_e.swMIL), kg));
            Assert.True(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_Custom), Len(swLengthUnit_e.swMM), lb));
            Assert.False(Units.IsImperial(Sys(swUnitSystem_e.swUnitSystem_Custom), Len(swLengthUnit_e.swMM), kg));
            Assert.False(Units.IsImperial(null));
        }

        [Fact]
        public void Unit_choices_keep_order_and_drop_strangers()
        {
            var list = Units.Choices("pressure", new[] { "psi", "bar", "MPa", "kgf/cm2", "lb" });
            Assert.Equal(new[] { "psi", "bar", "MPa", "kgf/cm2" }, list.ConvertAll(u => u.Ascii));
            Assert.Empty(Units.Choices("pressure", null));
        }
    }
}
