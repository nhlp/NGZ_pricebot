using PriceBotPipeline;
using Xunit;

/// <summary>RetryDelayParser: Gemini/Groq'un 429 yanıtlarındaki "ne kadar sonra tekrar dene"
/// ifadesini ayrıştıran paylaşılan yardımcı. Gemini "Please retry in Xs.", Groq "Please try again
/// in Xs." diyor — ikisi de aynı regex ile yakalanıyor.</summary>
public class RetryDelayParserTests
{
    [Theory]
    [InlineData("Please retry in 6.27678624s.", 6.28)]
    [InlineData("Please try again in 22.3875s", 22.39)]
    [InlineData("... RETRY IN 3S ...", 3.0)]
    public void GerçekSaglayiciIfadeleriDogruAyristirilir(string text, double expectedSeconds)
    {
        var delay = RetryDelayParser.ParseFromText(text);

        Assert.NotNull(delay);
        Assert.Equal(expectedSeconds, delay!.Value.TotalSeconds, precision: 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bu metinde hiç süre yok")]
    [InlineData("quota exceeded")]
    public void EslesmeyenMetinNullDoner(string? text)
    {
        Assert.Null(RetryDelayParser.ParseFromText(text));
    }

    [Fact]
    public void CokKisaSureAsgariSinirinAltinaDusmez()
    {
        var delay = RetryDelayParser.ParseFromText("Please retry in 0.05s.");

        Assert.NotNull(delay);
        Assert.True(delay!.Value.TotalSeconds >= 1.0);
    }

    [Fact]
    public void CokUzunSureUstSinirinUstuneCikmaz()
    {
        var delay = RetryDelayParser.ParseFromText("Please try again in 3600s.");

        Assert.NotNull(delay);
        Assert.True(delay!.Value.TotalSeconds <= 30.0);
    }

    [Theory]
    [InlineData(0.5, 1.0)]   // asgarinin altı -> asgariye kelepçelenir
    [InlineData(15.0, 15.0)] // aralıkta -> değişmez
    [InlineData(60.0, 30.0)] // azaminin üstü -> azamiye kelepçelenir
    public void ClampSinirlarinDisindakiDegerleriKelepceler(double inputSeconds, double expectedSeconds)
    {
        var clamped = RetryDelayParser.Clamp(TimeSpan.FromSeconds(inputSeconds));

        Assert.Equal(expectedSeconds, clamped.TotalSeconds, precision: 1);
    }
}
