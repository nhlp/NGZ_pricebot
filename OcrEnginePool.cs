// OcrEnginePool.cs (2026-08-07) — motor bağımsız OCR havuzu. Eskiden FullScanOcrPool (bkz.
// FullScanOcr.cs dosya sonu) sadece Tesseract'a özeldi; bu havuz IOcrEngine soyutlaması üzerinden
// çalışır ve Tesseract (FullScanOcr) veya PaddleOCR (PaddleScanOcr) örnekleriyle doldurulabilir.
// FullScanOcrPool BİLİNÇLİ OLARAK silinmedi — kullanılmıyor olması zararsız, geri dönüş için ekstra
// bir güvence. Worker.cs artık bu havuzu OcrEngineFactory.Create ile kurar.
namespace PriceBotPipeline;

using System.Collections.Concurrent;

public sealed class OcrEnginePool : IDisposable
{
    private readonly BlockingCollection<IOcrEngine> _pool = [];

    public int Size { get; }

    public OcrEnginePool(IReadOnlyCollection<IOcrEngine> engines)
    {
        Size = Math.Max(1, engines.Count);
        foreach (var engine in engines) _pool.Add(engine);
    }

    public T Run<T>(Func<IOcrEngine, T> work)
    {
        var engine = _pool.Take();
        try { return work(engine); }
        finally { _pool.Add(engine); }
    }

    public void Dispose()
    {
        // BlockingCollection'ın enumerator'ı tüketmeyen bir anlık görüntü döner (FullScanOcrPool'daki
        // aynı desen, bkz. FullScanOcr.cs).
        foreach (var engine in _pool) engine.Dispose();
        _pool.Dispose();
    }
}
