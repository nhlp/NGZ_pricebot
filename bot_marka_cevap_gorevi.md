# Görev: Gelen WhatsApp metin cevabını PriceBot klasörüne köprüle (marka cevabı)

## Bağlam

Bu bot ile aynı sunucuda **PriceBot.Worker** adında ayrı bir Windows servisi çalışıyor. Worker, botun
`C:\PriceBot\Incoming\<telefon_no>\Gonderim_...\` altına kaydettiği fotoğraf + Excel klasörlerini işleyip
fotoğraflara USD fiyat damgalıyor. Worker, fotoğraflardaki **markayı** otomatik tespit edemezse gönderene
şu mesajı yolluyor (bu kısım ÇALIŞIYOR — bot, `POST /api/whatsapp/internal/send`'e gelen boş `FilePath`'li
isteği metin mesajı olarak zaten iletiyor, değişiklik gerekmez):

> PriceBot: Gönderdiğiniz fotoğraflardaki ürünlerin markası otomatik tespit edilemedi. Lütfen markanın
> tam adını tek mesaj olarak yazınız (örnek: LİLAX).

**Sorun:** Kullanıcı markayı yazınca (örn. "bebly kids") botun normal sohbet/intent akışı devreye girip
"😕 Üzgünüm ne demek istediğinizi anlamadım" diyor ve cevap kayboluyor. Worker ise cevabı, sorunun
sorulduğu klasördeki bir dosyadan okumayı bekliyor. Botun bu cevabı yakalayıp dosyaya yazması gerekiyor.

## İstenen davranış

Gelen **her metin mesajında**, normal mesaj işleme akışından (intent çözme / "anlamadım" cevabı) **önce**
şu kontrol yapılacak:

1. Gönderenin numarasına ait `C:\PriceBot\Incoming\<telefon_no>\` altında, şu **üç koşulu birden**
   sağlayan bir `Gonderim_*` klasörü var mı ("bekleyen marka sorusu"):
   - `marka_sorusu.txt` **VAR**
   - `marka_cevap.txt` **YOK**
   - `islendi.txt` **YOK**
2. **Varsa:** gelen metni olduğu gibi (baş/son boşluk kırpma dışında değiştirmeden) o klasöre
   **`marka_cevap.txt`** adıyla **UTF-8** olarak yaz. Bu koşulu birden fazla klasör sağlıyorsa **en eski**
   olana yaz (klasör adları `Gonderim_yyyyMMdd_HHmmss_...` formatında olduğu için ada göre artan
   sıralamanın ilki yeterli).
3. Kullanıcıya kısa bir onay mesajı dön (örn. *"Teşekkürler! Marka bilginizi aldım, fiyatlarınız
   hazırlanıyor 🙏"*) ve normal "anlamadım" akışına **düşme**.
4. Bekleyen marka sorusu **yoksa** mevcut davranış aynen devam etsin (hiçbir şey değişmez).

## Kurallar / kenar durumlar

- Sadece **metin** mesajları için geçerli; medya mesajları (fotoğraf/dosya) mevcut kaydetme akışına
  aynen devam eder.
- `marka_cevap.txt` yazılacağı sırada zaten varsa **üzerine yaz** (kullanıcı kendini düzeltmiş demektir;
  worker klasörü işlemeden önceki son cevap geçerli olmalı).
- Klasörde `marka_cevap_red_*.txt` dosyaları görebilirsin — bunlar worker'ın arşivlediği, listeyle
  eşleşmemiş eski cevaplardır; **dokunma**. Yeni cevap her zaman `marka_cevap.txt` adına yazılır.
- Dosya adı birebir `marka_cevap.txt` (küçük harf), içerik düz metin, **UTF-8** (Türkçe karakterler
  bozulmadan; BOM olup olmaması fark etmez).
- Telefon → klasör eşlemesi: `<telefon_no>`, botun gelen dosyaları kaydederken kullandığı klasör adının
  aynısıdır — cevap eşlemesinde de aynı numara normalizasyonunu kullan.
- Cevap dosyası yazıldıktan sonra worker klasörü ~60-90 sn içinde işler (klasördeki son değişikliğin
  üzerinden 60 sn geçmesini bekler) — anlık işlenmemesi normaldir.
- Aynı numarada **birden fazla bekleyen klasör** olağan bir durumdur: worker, birden fazla markanın
  aynı anda gönderildiği paketleri marka gruplarına bölüp (`Gonderim_..._grup1`, `_grup2` gibi kardeş
  klasörler) her grup için ayrı soru sorabilir; soru metninde ilgili Excel'in dosya adı yazar. Bot
  tarafında davranış değişmez: her cevap, 2. maddedeki kurala göre **en eski** bekleyen klasöre yazılır
  (sorular hangi sırayla sorulduysa cevapların da o sırayla eşleşmesi beklenir — 5. kabul testi bu
  durumu zaten kapsıyor).
- Cevap worker'daki marka listesiyle eşleşmezse worker aynı numaraya önerilerle **yeni bir soru** yollar
  (*"PriceBot: '...' marka listesinde bulunamadı. Şunlardan birini mi kastettiniz: ..."*). Bu gönderim de
  mevcut send endpoint'inden gelir, ek geliştirme gerektirmez; kullanıcının yeni cevabı yine aynı
  mantıkla yakalanmalıdır (worker `marka_sorusu.txt`'yi kendisi yeniden oluşturur, bot sadece 1-4
  adımlarını uygular).

## Kabul testleri

1. `C:\PriceBot\Incoming\<test_no>\Gonderim_20260728_000000_test\` oluştur, içine boş bir
   `marka_sorusu.txt` koy. `<test_no>`'dan bota **"LİLAX"** yaz → klasörde içeriği `LİLAX` olan
   `marka_cevap.txt` oluşmalı, bot onay mesajı dönmeli, "anlamadım" **dönmemeli**.
2. Aynı numaradan ikinci bir metin **"PEPE"** → `marka_cevap.txt` üzerine yazılmış olmalı.
3. Bekleyen sorusu olmayan bir numaradan metin → normal bot davranışı değişmemeli.
4. Klasörde `islendi.txt` de varsa → bekleyen sayılmamalı, normal bot davranışı çalışmalı.
5. Aynı numarada iki bekleyen klasör varsa → cevap yalnızca **en eski** klasöre yazılmalı.
