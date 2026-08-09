# PriceBot.Worker — Windows Sunucuya Kurulum Rehberi

Bu rehber, projeyi geliştirme makinesinden alıp bir Windows sunucuda **Windows Service** olarak
çalıştırmak için adım adım ne yapman gerektiğini anlatır. Servis adı, appsettings.json içeriği ve
komutlar bu projenin gerçek yapılandırmasına göredir.

## 0. Genel bakış — sunucu topolojisi

- Worker, WhatsApp bot'una `https://asistyazilim.pakabulut.com:2304/...` üzerinden istek atıyor
  (`Worker.cs` içindeki `BotSendUrl` — eskiden `http://localhost:3978` idi, bot artık aynı sunucuda
  public domain + HTTPS üzerinden 2304 portunda dinliyor) → **worker ile bot yine aynı Windows sunucuda
  çalışmalı** (Incoming klasörü dosya sistemi üzerinden paylaşıldığı için), ama bot'a giden istek
  `localhost` değil, sunucunun kendi public domain adresi üzerinden gidiyor. Bu, sunucunun kendi public
  adresine "kendi kendine" (hairpin NAT) ulaşabilmesini gerektirir — adım 1.4'te bunu ayrıca test et.
- Worker, bot'un dosya yazdığı `C:\PriceBot\Incoming\` klasörünü doğrudan dosya sistemi üzerinden okuyor
  → bu klasör de aynı sunucuda olmalı (worker ile bot aynı makinede olduğu için bu zaten sağlanır).
- Worker, Nebim ERP'nin SQL Server'ına (`appsettings.json`'daki connection string,
  `asistyazilim.pakabulut.com,9023`) ağ üzerinden bağlanıyor → sunucudan bu adrese giden trafiğin
  firewall'da açık olması gerekir.

Yani pratikte: **WhatsApp bot'unun zaten çalıştığı sunucuya** worker'ı kuracaksın.

## 1. Sunucuda önkoşullar

1. **.NET 8 Runtime** kurulu olmalı (SDK değil, sadece Runtime yeterli — ama Tesseract/SkiaSharp gibi
   native kütüphaneler barındırdığı için "ASP.NET Core Runtime" değil, düz **.NET Runtime** yeterli).
   İndirme: `dotnet-runtime-8.0-win-x64.exe` (Microsoft .NET indirme sayfasından, "Run console apps"
   seçeneği). Kurulum sonrası doğrulama:
   ```
   dotnet --list-runtimes
   ```
   çıktısında `Microsoft.NETCore.App 8.0.x` görünmeli.
2. **Visual C++ Redistributable (x64)** — Tesseract (OCR) ve SkiaSharp native DLL'leri bazı sunucularda
   buna ihtiyaç duyar. Sunucuda zaten başka .NET/Windows uygulamaları çalışıyorsa muhtemelen kurulu.
   Değilse Microsoft'un "Visual C++ Redistributable" x64 paketini kur.
3. Nebim SQL Server'a **ağ erişimi**: sunucudan `asistyazilim.pakabulut.com:9023` adresine bağlantı
   test et:
   ```
   Test-NetConnection -ComputerName asistyazilim.pakabulut.com -Port 9023
   ```
   `TcpTestSucceeded : True` olmalı. Değilse firewall/VPN kontrolü gerekir.
4. WhatsApp bot'unun sunucuda kurulu ve `2304` portunda (HTTPS) dinlediğinden emin ol (bot ayrı bir
   proje, kurulumu bu rehberin kapsamında değil). Ayrıca **sunucunun kendi üzerinden** kendi public
   adresine ulaşabildiğini (hairpin NAT) doğrula — sunucuda:
   ```
   Test-NetConnection -ComputerName asistyazilim.pakabulut.com -Port 2304
   ```
   `TcpTestSucceeded : True` olmalı. Bazı NAT/firewall cihazları bir sunucunun kendi public adresine
   dışarıdan gelen bir istekmiş gibi geri dönmesine (hairpin/loopback NAT) izin vermez — bu durumda bu
   test başarısız olur ama dışarıdan (başka bir makineden) aynı adrese erişim çalışır; böyle bir sorunla
   karşılaşırsan bot'un ayrıca `localhost:2304` veya sunucunun iç IP'sinde de dinleyip dinlemediğini
   kontrol et ve gerekirse `Worker.cs`'teki `BotSendUrl`'ü ona göre (örn. `https://localhost:2304/...`)
   değiştirip yeniden `dotnet publish` al.

## 2. Geliştirme makinesinde: publish alma

Proje klasöründe (bu makinede, `c:\PriceBot\PriceBot.Worker`) şu komutu çalıştır:

```
dotnet publish PriceBot.Worker.csproj -c Release -r win-x64 --self-contained false -o C:\PriceBot\Publish
```

Ne yapıyor:
- `-c Release` — optimize edilmiş derleme (Debug değil).
- `-r win-x64` — hedef sunucu Windows x64 (bu proje zaten `PlatformTarget=x64`).
- `--self-contained false` — .NET Runtime'ın sunucuda kurulu olduğunu varsayar, çıktı küçük olur
  (adım 1'deki runtime kurulumu bu yüzden gerekli).
- `-o C:\PriceBot\Publish` — çıktının yazılacağı klasör (istediğin başka bir klasör de olabilir).

Çıktı klasöründe (`C:\PriceBot\Publish\`) neler olacak:

| Dosya/Klasör                          | Ne işe yarar |
|----------------------------------------|--------------|
| `PriceBot.Worker.exe`                  | Çalıştırılabilir dosya — servis bunu başlatacak |
| `PriceBot.Worker.dll` + `.pdb`         | Asıl uygulama kodu |
| `appsettings.json`                     | Nebim bağlantı dizesi — **kurulumdan önce mutlaka kontrol et (bkz. adım 3)** |
| `tessdata\`                            | Tesseract OCR dil/eğitim dosyaları — **eksik olursa OCR çalışmaz**, tüm klasör kopyalanmalı |
| `*.dll` (ClosedXML, SkiaSharp, Tesseract, Serilog, Microsoft.Data.SqlClient, Microsoft.Extensions.* vb.) | Bağımlılıklar, hepsi gerekli |
| `x64\`, `x86\` klasörleri + kök dizindeki `libSkiaSharp.dll`, `de\`, `es\` vb. dil klasörleri | SkiaSharp/Tesseract'ın native DLL'leri ve uydu kaynak dosyaları (bu projede ayrı bir `runtimes\` alt klasörü **yok**, doğrudan kök dizine yayılıyorlar) — **hepsi mutlaka kopyalanmalı**, silinirse OCR/damgalama çalışmaz |
| `Logs\`                                | İlk çalıştırmada otomatik oluşur, publish anında yok — kopyalamana gerek yok |

**Kısacası: `C:\PriceBot\Publish\` klasörünün tamamını kopyala, tek tek dosya seçme.**

## 3. `appsettings.json`'ı kontrol et / düzenle

Sunucuya kopyalamadan önce (veya kopyaladıktan hemen sonra sunucuda) `C:\PriceBot\Publish\appsettings.json`
dosyasını aç:

```json
{
  "ConnectionStrings": {
    "Nebim": "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True;"
  }
}
```

- `ConnectionStrings:Nebim` — sunucudan erişilebilir olduğunu adım 1.3'te test ettiğin adres olmalı.
- Bu dosya şifre içerdiği için **sunucuda dosya izinlerini kısıtla** (sadece servis hesabı ve
  yöneticiler okuyabilsin) ve dosyayı hiçbir yere (git, paylaşım, mail) gönderme.
- 2026-08-08: `ExtraRecipients` özelliği hiç kullanılmadığı için koddan tamamen kaldırıldı — her işlenen
  görsel artık her zaman sadece gönderen numaraya gider. Opsiyonel olarak `OcrParallelism`,
  `OcrCacheCapacity`, `OcrRecycleHours` anahtarları eklenebilir (bkz. CLAUDE.md "OCR performansı") —
  hiçbiri zorunlu değil, yoksa kod içi varsayılanlar kullanılır.

## 4. Dosyaları sunucuya taşı

`C:\PriceBot\Publish\` klasörünün tamamını sunucuda kalıcı olarak duracağı yere kopyala, örn.
`C:\PriceBot\Publish\` (aynı yol, farklı sunucuda). Kopyalama yöntemi ortamına göre değişir: RDP ile
sürükle-bırak, `robocopy` (ağ üzerinden), sıkıştırıp taşıma, vb. — hepsi geçerli, önemli olan **klasörün
bütün içeriğinin** (özellikle `tessdata\` ve `runtimes\`) eksiksiz gitmesi.

```
robocopy C:\PriceBot\Publish \\SUNUCU_ADI\C$\PriceBot\Publish /E
```

## 5. Windows Service olarak kaydet

Sunucuda **yönetici olarak** bir PowerShell/CMD aç:

```
sc.exe create PriceBotWorker binPath= "C:\PriceBot\Publish\PriceBot.Worker.exe" start= auto
```

- **PowerShell'de `sc` yazma, `sc.exe` yaz** — PowerShell'de `sc`, `Set-Content` cmdlet'inin yerleşik
  takma adıdır (`sc.exe` değil) ve `binPath=` gibi argümanları anlamadığı için hataya düşer. CMD'de bu
  sorun yoktur ama PowerShell'de her zaman `sc.exe` kullan.
- `binPath=` ve `start=` sonrasındaki **boşluk zorunlu** (`sc.exe`'nin garip ama bilinen bir kuralı).
- `start= auto` → sunucu her yeniden başladığında servis otomatik başlar.
- Servis çalıştıran hesap belirtilmezse varsayılan olarak `LocalSystem` kullanılır — bu hesabın dosya
  sistemine (Incoming/Publish/Logs klasörleri) ve ağa (Nebim DB, bot'un 2304 portu) erişimi olur, genelde
  ek bir şey yapmana gerek yoktur. Kısıtlı bir hizmet hesabı kullanmak istersen `sc.exe config
  PriceBotWorker obj= "DOMAIN\hesap" password= "..."` ile ayrıca ayarlanır.

Servisi başlat:

```
sc.exe start PriceBotWorker
```

Durumunu kontrol et:

```
sc.exe query PriceBotWorker
```

`STATE` alanı `RUNNING` olmalı. `STOPPED` görünüyorsa adım 7'deki (sorun giderme) loglara bak.

## 6. Doğrulama

1. **Event Viewer** → Windows Logs → Application → Source: `PriceBotWorker` filtrele. Başlangıç
   mesajını görmelisin: *"PriceBot Worker host başlatılıyor (servis modu: True)"*.
2. **Log dosyası**: `C:\PriceBot\Publish\Logs\pricebot-YYYYMMDD.log` oluşmuş ve büyüyor olmalı.
3. Test için `C:\PriceBot\Incoming\` altına (gerçek bir müşteri numarası **değil**, izole bir test
   numarası klasörü altında) sentetik bir `.xlsx` + görselle bir `Gonderim_...` klasörü oluştur, ~70 sn
   bekle, log dosyasında/Event Viewer'da işleme kaydını gör, klasörde `islendi.txt` ve `Islenmis\`
   oluştuğunu doğrula. (Bkz. proje `CLAUDE.md`'deki test uyarısı — gerçek müşteri klasörlerini kullanma.)

## 7. Sorun giderme

- **Servis `RUNNING`'e geçmiyor / hemen duruyor**: Event Viewer → Application loglarına bak (servis
  başlarken atılan ilk hata orada görünür — appsettings.json okunamıyor, connection string hatalı,
  tessdata bulunamıyor gibi sebepler genelde ilk saniyelerde patlar).
- **"appsettings.json bulunamadı" / OCR hiç eşleşme bulmuyor**: publish klasöründeki `appsettings.json`
  ve `tessdata\` eksik/yanlış yere kopyalanmış olabilir — adım 2'deki tabloyu kontrol et.
- **Nebim kuru hiç bulunamıyor uyarısı sürekli tekrarlıyor**: adım 1.3'teki ağ testini tekrarla,
  connection string'i kontrol et.
- **Görseller damgalanıyor ama WhatsApp'a gitmiyor**: bot'un gerçekten `2304`'te dinlediğini doğrula
  (`Test-NetConnection -ComputerName asistyazilim.pakabulut.com -Port 2304`), `islendi.txt`
  yazılmamışsa gönderim adımı hata veriyor demektir — log dosyasında `Gönderim hatası` satırlarını ara.
  Hairpin NAT sorunuysa (bkz. adım 1.4) sunucu kendi public adresine ulaşamıyor olabilir.
- **Loglar hiç yazılmıyor**: servis hesabının `C:\PriceBot\Publish\Logs\` klasörüne yazma izni olduğundan
  emin ol (varsayılan `LocalSystem` ile genelde sorun olmaz).

## 8. Güncelleme prosedürü

Kod değiştiğinde:

```
sc.exe stop PriceBotWorker
dotnet publish PriceBot.Worker.csproj -c Release -r win-x64 --self-contained false -o C:\PriceBot\Publish_yeni
```
Yeni çıktıyı sunucudaki `C:\PriceBot\Publish\` üzerine kopyala (appsettings.json'ı ezmemeye dikkat et —
sunucudaki gerçek connection string sürümünü koru, sadece `.exe/.dll` ve native/dil klasörleri —
`x64\`, `x86\`, `tessdata\` vb. — güncellensin), sonra:
```
sc.exe start PriceBotWorker
```

## 9. Kaldırma

```
sc.exe stop PriceBotWorker
sc.exe delete PriceBotWorker
```
