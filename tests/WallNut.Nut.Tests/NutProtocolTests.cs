namespace WallNut.Nut.Tests;

public class NutProtocolTests
{
    [Fact]
    public void Tokenize_splits_words_and_unquotes_values()
    {
        var t = NutProtocol.Tokenize("VAR ups ups.status \"OL CHRG\"");
        Assert.Equal(["VAR", "ups", "ups.status", "OL CHRG"], t);
    }

    [Fact]
    public void Tokenize_handles_escaped_quotes_and_backslashes()
    {
        var t = NutProtocol.Tokenize("UPS ups \"Back \\\"Office\\\" C:\\\\\"");
        Assert.Equal(["UPS", "ups", "Back \"Office\" C:\\"], t);
    }

    [Fact]
    public void Tokenize_keeps_empty_quoted_values()
    {
        Assert.Equal(["VAR", "ups", "ups.serial", ""], NutProtocol.Tokenize("VAR ups ups.serial \"\""));
    }

    [Fact]
    public void Tokenize_rejects_unterminated_quote()
    {
        Assert.Throws<FormatException>(() => NutProtocol.Tokenize("VAR ups x \"oops"));
    }

    [Fact]
    public void Quote_round_trips_through_Tokenize()
    {
        const string value = "pa ss\"w\\rd";
        Assert.Equal(value, NutProtocol.Tokenize(NutProtocol.Quote(value)).Single());
    }

    [Theory]
    [InlineData("ACCESS-DENIED", NutErrorCode.AccessDenied)]
    [InlineData("UNKNOWN-UPS", NutErrorCode.UnknownUps)]
    [InlineData("DATA-STALE", NutErrorCode.DataStale)]
    [InlineData("SOMETHING-NEW", NutErrorCode.Unknown)]
    public void Error_codes_are_parsed(string raw, NutErrorCode expected)
    {
        Assert.Equal(expected, NutException.Parse(raw));
    }
}

public class UpsStatusTests
{
    [Fact]
    public void Online_and_charging()
    {
        var s = UpsStatus.Parse("OL CHRG");
        Assert.True(s.Has(UpsStatusFlags.OnLine | UpsStatusFlags.Charging));
        Assert.False(s.IsOnBattery);
        Assert.False(s.IsCritical);
    }

    [Fact]
    public void On_battery_alone_is_not_critical()
    {
        var s = UpsStatus.Parse("OB DISCHRG");
        Assert.True(s.IsOnBattery);
        Assert.False(s.IsCritical);
    }

    [Theory]
    [InlineData("OB LB")]
    [InlineData("OB DISCHRG LB")]
    [InlineData("OL FSD")]
    public void Critical_when_on_battery_and_low_or_forced_shutdown(string raw)
    {
        Assert.True(UpsStatus.Parse(raw).IsCritical);
    }

    [Fact]
    public void Unknown_words_are_ignored()
    {
        Assert.Equal(UpsStatusFlags.OnLine, UpsStatus.Parse("OL ECO").Flags);
        Assert.Equal(UpsStatusFlags.None, UpsStatus.Parse(null).Flags);
    }
}
