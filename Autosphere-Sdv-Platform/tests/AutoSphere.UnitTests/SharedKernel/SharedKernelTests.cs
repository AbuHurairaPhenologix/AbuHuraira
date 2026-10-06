using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.UnitTests.SharedKernel;

public sealed class SoftwareVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("0.0.1", "0.0.10", -1)]
    public void Versions_compare_numerically(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(SoftwareVersion.Parse(left).CompareTo(SoftwareVersion.Parse(right))));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.a.0")]
    [InlineData("-1.0.0")]
    [InlineData("")]
    [InlineData(" 1. 0.0")]
    public void Invalid_versions_are_rejected(string value)
    {
        Assert.False(SoftwareVersion.TryParse(value, out _));
    }

    [Fact]
    public void Operators_and_equality_work()
    {
        Assert.True(SoftwareVersion.Parse("1.1.0") > SoftwareVersion.Parse("1.0.9"));
        Assert.Equal(SoftwareVersion.Parse("1.2.3"), new SoftwareVersion(1, 2, 3));
        Assert.Equal("1.2.3", new SoftwareVersion(1, 2, 3).ToString());
    }
}

public sealed class DtcCodeTests
{
    [Theory]
    [InlineData("P0A7E", 0x0A7E)]
    [InlineData("U0100", 0xC100)]
    [InlineData("U0140", 0xC140)]
    [InlineData("B1234", 0x9234)]
    [InlineData("C0035", 0x4035)]
    [InlineData("P3FFF", 0x3FFF)]
    public void Codes_encode_to_sae_two_byte_format(string code, int expected)
    {
        var dtc = DtcCode.Parse(code);

        Assert.Equal((ushort)expected, dtc.ToUInt16());
        Assert.Equal(dtc, DtcCode.FromUInt16((ushort)expected));
    }

    [Fact]
    public void Uds_three_byte_dtc_appends_failure_type_byte()
    {
        var dtc = DtcCode.Parse("P0A2F");

        Assert.Equal(0x0A2F00u, dtc.ToUdsDtc());
        Assert.Equal(dtc, DtcCode.FromUdsDtc(0x0A2F00));
    }

    [Theory]
    [InlineData("X0100")]
    [InlineData("P4000")]
    [InlineData("P0A7")]
    [InlineData("P0AZE")]
    public void Invalid_codes_are_rejected(string code)
    {
        Assert.False(DtcCode.TryParse(code, out _));
    }

    [Fact]
    public void Catalogue_describes_known_and_unknown_codes()
    {
        Assert.Equal("Battery Thermal Fault", KnownDtcs.Describe(DtcCode.Parse("P0A7E")).FaultCategory);
        Assert.Equal(DtcSeverity.Critical, KnownDtcs.Describe(DtcCode.Parse("U0100")).Severity);
        Assert.Equal("Unclassified Fault", KnownDtcs.Describe(DtcCode.Parse("P1234")).FaultCategory);
    }
}
