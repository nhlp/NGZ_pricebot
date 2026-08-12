using PriceBotPipeline;
using Xunit;

/// <summary>Gerçek üretim vakası (2026-08-12): "ALİSA PİYASA 3 İP" klasöründeki bir MESSINHO
/// görselinde OCR sadece "57-099"u okudu (güven 100, tam eşleşme) ama worker görsele İKİNCİ bir
/// fiyat satırı daha ("57-097", güven 60, fuzzy) bastı — Excel'de GERÇEKTEN var olan ama bu
/// FOTOĞRAFTA hiç yazmayan, "57-099"a Levenshtein mesafe 1 komşu bir stil kodu. Kök neden:
/// TryExtractHyphen'in "57-099" okumasından ürettiği kısaltılmış önek adayı ("57-09") tam
/// eşleşen "57-099"un AYNI kanıtıydı ama string eşitliği farklı olduğu için (`"57-09" !=
/// "57-099"`) eski selfRef kontrolü bunu bağımsız kanıt sanıyordu. Bu testler
/// `ComputeFuzzyCandidates`'ın (bkz. PaddleScanOcrMatching.cs) bu açığı kapattığını VE
/// orijinal meşru çok-kod senaryolarını (bkz. proje hafızası "OCR çoklu-kod fuzzy düzeltmesi
/// v9-v11") kırmadığını doğrular.</summary>
public class PaddleScanOcrMatchingTests
{
    private static Dictionary<string, float> Extracted(params (string Candidate, float Conf)[] items) =>
        items.ToDictionary(i => i.Candidate, i => i.Conf);

    [Fact]
    public void PrefixTruncationOfAnchor_IsFlaggedSelfReferential_57099_57097Vakasi()
    {
        // "57-099" okundu ve tam eşleşti; TryExtractHyphen aynı token'dan "57-0"/"57-09"/"57-099"
        // önek ailesini üretir (gerçek davranış) — burada sadece bug'ı tetikleyen "57-09" ve
        // "57-099"u simüle etmek yeterli.
        var extracted = Extracted(("57-0", 99f), ("57-09", 99f), ("57-099", 99f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("57-099", 100f) };
        // "57-097" Excel'de gerçekten var ama bu görselde hiç OCR token'ı yok — sadece fuzzy
        // aramanın "henüz eşleşmemiş kod" havuzunda.
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "57-097" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.True(found.ContainsKey("57-097"));
        Assert.True(found["57-097"].SelfReferential,
            "57-097, sadece 57-099'un kendi okumasının kısaltılmış öneğinden (57-09) türetiliyor — " +
            "bağımsız kanıt değil, self-referential işaretlenmeli (kabul için yaş/stil çapraz " +
            "doğrulaması gerekmeli, doğrudan otomatik kabul edilmemeli).");
    }

    [Fact]
    public void FarHyphenatedCode_ExcludedFromScope_NearbyAnchorDeltaRestored()
    {
        // Eski davranış: `long.TryParse("57-099", ...)` tire yüzünden başarısız olup anchors.Count
        // == 0'a düşüyor, scope TÜM eşleşmemiş kodlara genişliyordu (NearbyAnchorDelta=3 kısıtlaması
        // tire'li kodlarda sessizce devre dışıydı). "57-150", anchor "57-099"dan (delta 51) çok
        // uzak — SplitCodeKey ile scope'a hiç girmemeli, candidate'in ona 1 Levenshtein mesafede
        // olması önemsiz.
        var extracted = Extracted(("57-099", 99f), ("57-149", 45f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("57-099", 100f) };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "57-150" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.False(found.ContainsKey("57-150"),
            "57-150, 57-099 anchor'ından numaraca çok uzak (delta 51 > 3) — tire'li kod olsa bile " +
            "NearbyAnchorDelta kapsamına hiç girmemeli.");
    }

    [Fact]
    public void ExactTextSelfReference_StillDetected_Cuento6541_6542()
    {
        // v10.1'in orijinal senaryosu (bkz. proje hafızası): doğru okunan "6541" kendi
        // komşusu "6542" için tek kanıt. Bu refactordan ETKİLENMEMELİ — hâlâ self-referential.
        var extracted = Extracted(("6541", 95f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("6541", 95f) };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "6542" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.True(found.ContainsKey("6542"));
        Assert.True(found["6542"].SelfReferential);
    }

    [Fact]
    public void SiblingHyphenatedCandidate_NotPrefixFamily_IndependentEvidenceRecognized()
    {
        // "57-098" ile "57-099" AYNI uzunlukta, sadece son hane farklı — biri diğerinin
        // kısaltması DEĞİL (IsPrefixFamily bunu ayırt etmeli). "57-098" görselde GERÇEKTEN ayrı
        // bir OCR okuması olsaydı (ör. ikinci bir yaş/beden bloğu), bu bağımsız kanıt sayılmalı.
        var extracted = Extracted(("57-099", 99f), ("57-098", 90f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("57-099", 99f) };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "57-097" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.True(found.ContainsKey("57-097"));
        Assert.False(found["57-097"].SelfReferential,
            "57-098, 57-099'un bir ÖNEĞİ değil (aynı uzunlukta, farklı son hane) — bağımsız bir " +
            "OCR okuması olarak ele alınmalı, self-referential sayılmamalı.");
        Assert.Equal("57-098", found["57-097"].Candidate);
    }

    [Fact]
    public void EqualConfidenceTie_PrefersIndependentEvidenceOverSelfReferential()
    {
        // Aynı güvende hem kendi-kanıt (57-09, anchor'ın öneği) hem bağımsız (57-098, ayrı okuma)
        // aday varsa, Dictionary/iterasyon sırasına bağlı ÖRTÜK bir seçim yerine BİLİNÇLİ olarak
        // bağımsız olan tercih edilmeli (2026-08-12 öncesi: hangisinin "önce" işlendiği selfRef
        // sonucunu belirliyordu — kırılgandı).
        var extracted = Extracted(("57-09", 90f), ("57-098", 90f), ("57-099", 90f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("57-099", 90f) };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "57-097" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.True(found.ContainsKey("57-097"));
        Assert.False(found["57-097"].SelfReferential);
        Assert.Equal("57-098", found["57-097"].Candidate);
    }

    [Fact]
    public void LetterPrefixFamily_TreatedSameAsHyphenFamily_V029Vakasi()
    {
        // Harf önekli kodlar (V-029 gibi, gerçek "mini pakel" vakası) TryExtractLetterPrefix ile
        // aynı büyüyen-önek ailesini üretiyor ("V-0", "V-02", "V-029") — IsPrefixFamily bunları da
        // kapsamalı.
        var extracted = Extracted(("V-0", 90f), ("V-02", 90f), ("V-029", 90f));
        var candidates = extracted.Keys.ToList();
        var alreadyMatched = new List<CodeMatch> { new("V-029", 90f) };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "V-027" };

        var found = PaddleScanOcr.ComputeFuzzyCandidates(candidates, extracted, unmatched, alreadyMatched);

        Assert.True(found.ContainsKey("V-027"));
        Assert.True(found["V-027"].SelfReferential);
    }
}
