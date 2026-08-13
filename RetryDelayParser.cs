using System.Globalization;
using System.Text.RegularExpressions;

namespace PriceBotPipeline;

/// <summary>Görü-tespiti sağlayıcılarının (Gemini, Groq) 429 yanıtlarındaki insan-okunur "ne kadar
/// sonra tekrar dene" ifadesini ayrıştıran PAYLAŞILAN yardımcı (2026-08-13) — hem
/// GeminiVisionClassifierLabelResolver.ParseRetryDelay (Google: "Please retry in 6.27s.") hem
/// GroqVisionClassifierLabelResolver.ParseRetryDelay (Groq: "Please try again in 22.3s.") bunu
/// kullanır; iki sağlayıcının metin ifadesi neredeyse aynı ama fiil farklı ("retry"/"try again"),
/// tek bir regex'te birleştirildi. Gemini'nin AYRICA yapılandırılmış bir <c>google.rpc.RetryInfo</c>
/// alanı var (kendi dosyasında ayrı ele alınır, öncelikli); Groq'ta böyle bir alan gözlenmedi
/// (2026-08-13 canlı testte sadece mesaj metni vardı), o yüzden Groq doğrudan bu metni kullanır.</summary>
internal static class RetryDelayParser
{
    // RegexOptions.CultureInvariant ŞART: IgnoreCase TEK BAŞINA .NET'te CurrentCulture'a göre
    // büyük/küçük harf dönüşümü yapar — Türkçe kültürde büyük "I" küçük "ı"ya (noktasız) döner,
    // "in"e DEĞİL ("Türkçe I problemi", çok bilinen bir .NET/ICU tuzağı). Bu servis Türkçe
    // ortamda çalıştığı için (appsettings/log'lar Türkçe) CultureInvariant olmadan bu regex,
    // sağlayıcı yanıtı büyük harfle gelirse ("RETRY IN...") sessizce eşleşmeyebilirdi — gerçek
    // Gemini/Groq yanıtları normal cümle biçimi kullanıyor (bu yüzden şu ana kadar hiç sorun
    // çıkmadı) ama CultureInvariant olmadan bu kırılgan bir varsayıma dayanıyordu.
    private static readonly Regex TextPattern = new(
        @"(?:retry|try again) in\s+([\d.]+)\s*s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);

    /// <summary>Üst sınır bilinçli olarak kısa tutuldu — bu süre artık (2026-08-13'ten beri)
    /// senkron bir bekleme İÇİN değil, Worker.cs'in "bu görseli erteleyip diğerlerini işledikten
    /// sonra tekrar dene" kararı için bir İPUCU olarak kullanılıyor (bkz. IProductCodeClassifier.cs
    /// RetryAfter dokümantasyonu) — yine de aşırı uzun bir öneriye (ör. günlük kota tükenmişse)
    /// körü körüne uyulmaması için makul bir tavan konuldu.</summary>
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    internal static TimeSpan Clamp(TimeSpan delay) =>
        delay < MinDelay ? MinDelay : delay > MaxDelay ? MaxDelay : delay;

    internal static TimeSpan? ParseFromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var match = TextPattern.Match(text);
        if (!match.Success) return null;

        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Clamp(TimeSpan.FromSeconds(seconds))
            : null;
    }
}
