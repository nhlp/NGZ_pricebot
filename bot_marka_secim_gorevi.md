# Görev: Marka sorusunu WhatsApp interaktif liste (seçim) mesajı olarak gönder

## Bağlam

Bu, [bot_marka_cevap_gorevi.md](bot_marka_cevap_gorevi.md)'nin **üzerine eklenen** bir geliştirme —
oradaki görev (gelen metin cevabını `marka_cevap.txt`'ye yazma) hâlâ olduğu gibi geçerli ve gerekli.
Bu görev SADECE marka sorusunun **gönderiliş biçimini** zenginleştiriyor: düz metin yerine, mümkünse
kullanıcının dokunarak seçebileceği bir liste.

Worker artık markayı tam bulamadığında (hem ilk soruda hem "cevabınız listede yok, tekrar sorayım"
turunda) `POST /api/whatsapp/internal/send` isteğine, mevcut `ToNumber`/`MessageText`/`FilePath`
alanlarının **yanına**, opsiyonel bir alan daha ekliyor:

```json
{
  "ToNumber": "905xxxxxxxxx",
  "MessageText": "PriceBot: Gönderdiğiniz fotoğraflardaki ürünlerin markası otomatik tespit edilemedi ('liste.xlsx' listesindeki ürünler). Şunlardan biri olabilir mi: DECO SPORT, DECO KIDS? Bunlardan biri doğruysa onu yazabilir, değilse markanın tam adını yazabilirsiniz.",
  "FilePath": "",
  "Options": ["DECO SPORT", "DECO KIDS", "Diğer (markayı kendim yazacağım)"]
}
```

## İstenen davranış

### 1) Gönderim tarafı — liste mesajı oluşturma

`Options` alanı **doluysa** (null/boş değilse) ve `FilePath` boşsa, bu isteği düz metin yerine bir
**WhatsApp Cloud API interactive list message** olarak gönder:

- **Body** (mesaj gövdesi) = `MessageText` (aynen, kısaltmadan).
- **Action / Button** (listeyi açan buton) = kısa sabit bir etiket, örn. `"Seç"` ya da `"Markayı Seç"`
  (Worker bunu göndermiyor, bot tarafında sabit tutulabilir).
- **Section rows** = `Options` dizisindeki HER eleman için bir satır, **sırayla**:
  - `title` = seçeneğin metni. WhatsApp satır başlığı sınırı **24 karakter** — bundan uzunsa kısalt
    (örn. `"DECO KIDS WEAR"` gibi 24'ü aşan bir marka adı gelirse) ve **tam metni `description`
    alanına** koy (description sınırı 72 karakter, bu genelde yeterli).
  - `id` = stabil, tekrarlanabilir bir kimlik (örn. `opt_0`, `opt_1`, ... — sıra numarasına göre),
    içeriği önemli değil, sadece webhook'ta hangi satırın seçildiğini ayırt etmeye yarar.
- Liste en fazla **10 satır** taşıyabilir; Worker şu an en fazla 5 tahmin + 1 "Diğer" = 6 satır
  gönderiyor, sınırı aşma riski yok ama yine de üst sınırı aşan bir istek gelirse (ileride
  değişebilir) satırları 10'da kes.
- `Options` **null/boşsa** (bugünkü normal davranış, marka bulunamadığında hiç makul tahmin yoksa):
  hiçbir şey değişmez, `MessageText`'i **düz metin** olarak gönder (bkz. bot_marka_cevap_gorevi.md).

Kaynak: [WhatsApp Cloud API — Interactive List Messages](https://developers.facebook.com/docs/whatsapp/cloud-api/messages/interactive-list-messages/)
(satır başlığı 24, açıklama 72, bölüm başlığı 24 karakter; en fazla 10 satır).

### 2) Cevap tarafı — seçilen satırı yakala

Kullanıcı listeden bir satır seçtiğinde WhatsApp, gelen webhook'a bunu **metin mesajı DEĞİL**,
`type: "interactive"` (`interactive.type: "list_reply"`) olarak yollar. Bu olayı da,
[bot_marka_cevap_gorevi.md](bot_marka_cevap_gorevi.md)'deki metin-mesajı kontrolüyle **aynı üç koşulu**
(`marka_sorusu.txt` var, `marka_cevap.txt` yok, `islendi.txt` yok) kontrol ederek yakala:

1. Gönderenin bekleyen (en eski) marka sorusu klasörü var mı? (aynı kural, aynı klasör seçimi)
2. Varsa: seçilen satırın **`title`** metnini (id'sini DEĞİL — Worker karşılaştırmayı görünen metinle
   yapıyor) olduğu gibi `marka_cevap.txt`'ye UTF-8 yaz. **Aynen bir kullanıcının o metni elle
   yazmış gibi davran** — "Diğer" seçilmiş olsa bile (`"Diğer (markayı kendim yazacağım)"` metnini
   olduğu gibi yaz); Worker bu özel metni tanıyıp otomatik olarak listesiz, düz "markanın tam adını
   yazınız" sorusuna döner — **bot tarafında "Diğer" için özel bir davranış gerekmez**, sadece seçilen
   satırın görünen metnini normal cevap gibi ilet.
   - Not: eğer satır başlığı 24 karakter sınırı yüzünden kısaltılmış ve tam metin `description`'a
     konmuşsa, `marka_cevap.txt`'ye **`description`'daki (kısaltılmamış) metni** yaz, kısaltılmış
     `title`'ı değil.
3. Kullanıcıya kısa bir onay mesajı dön (bot_marka_cevap_gorevi.md'deki 3. madde ile aynı).
4. Bekleyen marka sorusu yoksa (interactive mesaj ama ilgili klasör yok) normal davranış değişmesin.

### 3) Geriye dönük uyumluluk / bozulma riski yok

- `Options` alanı yeni ve opsiyonel — eski istekler (ör. normal görsel gönderimi, müşteri raporu
  metni) bu alanı hiç içermez veya `null` gönderir, davranış değişmez.
- Worker, liste gönderimi başarısız olursa (örn. bot henüz bu özelliği desteklemiyorsa `Options`'ı
  yok sayıp `MessageText`'i düz metin olarak gönderirse) yine de doğru çalışır — `MessageText`
  seçenekleri zaten metin içinde de listeler (bkz. örnek istek), sadece dokunarak seçme kolaylığı
  kaybolur. Yani bu görev bot tarafında geciktirilse/atlansa bile **hiçbir şey kırılmaz**.

## Kurallar / kenar durumlar

- `Options` içindeki son eleman HER ZAMAN `"Diğer (markayı kendim yazacağım)"` sabit metnidir (Worker
  tarafından ekleniyor) — bot bunu özel tanımak ZORUNDA değil, sadece normal bir satır gibi işlensin.
- Marka adları Türkçe karakter içerebilir (İ, Ş, Ğ, Ü, Ö, Ç) — satır başlığı/açıklaması UTF-8 olarak
  gönderilmeli, bozulmamalı.
- Aynı numarada birden fazla bekleyen klasör olması durumunda kural bot_marka_cevap_gorevi.md ile
  birebir aynı: cevap **en eski** bekleyen klasöre yazılır.

## Kabul testleri

1. `Options: ["DECO SPORT", "DECO KIDS", "Diğer (markayı kendim yazacağım)"]` ile bir istek gönder →
   kullanıcıya 3 satırlık bir liste mesajı gitmeli (düz metin değil).
2. Listeden **"DECO SPORT"** seç → ilgili klasörde içeriği `DECO SPORT` olan `marka_cevap.txt`
   oluşmalı, bot onay mesajı dönmeli.
3. Listeden **"Diğer (markayı kendim yazacağım)"** seç → `marka_cevap.txt` içeriği bu metnin
   kendisi olmalı (worker bunu tanıyıp otomatik olarak tekrar, listesiz bir soru gönderecek — bu
   ikinci soruya bot'un normal metin-cevabı akışıyla cevap verilebilmeli, bkz.
   bot_marka_cevap_gorevi.md kabul testleri).
4. `Options` **olmayan** (null) bir istek → eskisi gibi düz metin gitmeli, davranış değişmemeli.
5. 24 karakterden uzun bir seçenek (örn. `"MOTHER&ÇOJOK TEKSTİL ÜRÜNLERİ"`) gelirse → satır başlığı
   kısaltılmalı ama seçildiğinde `marka_cevap.txt`'ye tam (kısaltılmamış) metin yazılmalı.
