<div align="center">

# VORTEX Public Kurulum Rehberi

**Public Server ve Web arayüzünü güvenli, secret-free ve doğrulanabilir bir yerel geliştirme ortamında çalıştırma rehberi.**

[← Ana README](../README.md) · [Dokümantasyon Merkezi](README.md) · [Mimari](ARCHITECTURE.md)

</div>

---

> [!IMPORTANT]
> Bu rehber yalnız public `Vortex.Server.Public` ve `Vortex.Web` bileşenlerini kapsar. Private Worker, Hermes, Desktop runtime veya üretim deployment bilgisi içermez.

## İçindekiler

- [Kapsam](#kapsam)
- [Güvenlik sınırı](#güvenlik-sınırı)
- [Ön koşullar](#ön-koşullar)
- [Hızlı başlangıç](#hızlı-başlangıç)
- [Repository doğrulaması](#repository-doğrulaması)
- [Server yapılandırması](#server-yapılandırması)
- [Web yapılandırması](#web-yapılandırması)
- [Ortam değişkenleri](#ortam-değişkenleri)
- [Yerel veri dizini](#yerel-veri-dizini)
- [Public Server'ı çalıştırma](#public-serverı-çalıştırma)
- [Web arayüzünü çalıştırma](#web-arayüzünü-çalıştırma)
- [Restore, build ve test](#restore-build-ve-test)
- [Çalışma zamanı doğrulaması](#çalışma-zamanı-doğrulaması)
- [Güncelleme akışı](#güncelleme-akışı)
- [Sorun giderme](#sorun-giderme)
- [Güvenlik kontrol listesi](#güvenlik-kontrol-listesi)
- [Kurulum görünümü](#kurulum-görünümü)
- [İlgili belgeler](#ilgili-belgeler)

## Kapsam

Bu rehber, public VORTEX çözümünü yerel geliştirme ortamında yapılandırmak ve doğrulamak için kanonik başlangıç akışını açıklar.

Kapsanan bileşenler:

- `VortexAI.Public.sln`
- `Vortex.Server.Public`
- `Vortex.Web`
- `Vortex.Contracts`
- `Vortex.Public.Tests`
- public örnek yapılandırmalar

Kapsanmayan bileşenler:

- private Desktop runtime,
- LocalAgent,
- Hermes ve HermesWorker,
- Worker servisleri,
- üretim deployment,
- gerçek token veya API anahtarları,
- gerçek kullanıcı verileri.

## Güvenlik sınırı

> [!CAUTION]
> Aşağıdaki dosya ve değerleri repository'ye eklemeyin:
>
> - gerçek `appsettings.json`,
> - `.env` dosyaları,
> - JWT signing key,
> - kullanıcı token'ları,
> - veritabanları,
> - loglar,
> - sertifikalar ve private key'ler,
> - üretim endpointleri,
> - build çıktıları.

Örnek yapılandırmalar yalnız placeholder içermelidir. Gerçek değerler yerel secret store, kullanıcı ortam değişkeni veya commit edilmeyen yerel yapılandırma üzerinden sağlanmalıdır.

## Ön koşullar

### Zorunlu

- .NET 8 SDK
- Git
- Yerel ve yazılabilir bir veri dizini
- En az 32 UTF-8 byte içeren benzersiz JWT signing key

### Önerilen

- PowerShell 7 veya güncel Windows PowerShell
- Visual Studio 2022, Visual Studio Code veya Rider
- Kaynak kontrolünde `.gitignore` doğrulaması
- Ayrı geliştirme ve test veri dizinleri

### Kurulum doğrulaması

```powershell
# [SALT-OKUNUR] Windows PowerShell

git --version
dotnet --version
dotnet --info
```

Beklenti:

- `dotnet --version` çıktısı `8.x` sürüm ailesini göstermelidir.
- `dotnet --info` kullanılabilir SDK ve runtime'ları listelemelidir.
- Git komutları repository klonlamak için kullanılabilir olmalıdır.

## Hızlı başlangıç

```powershell
# 1. Depoyu klonlayın
git clone <REPOSITORY_URL>
cd <REPOSITORY_DIRECTORY>

# 2. Public çözümü restore edin
dotnet restore VortexAI.Public.sln

# 3. Release yapılandırmasında derleyin
dotnet build VortexAI.Public.sln -c Release --no-restore

# 4. Public testleri çalıştırın
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

Ardından iki terminal açın:

```powershell
# Terminal 1 — Public Server
dotnet run --project Vortex.Server.Public/Vortex.Server.Public.csproj
```

```powershell
# Terminal 2 — Web
dotnet run --project Vortex.Web/Vortex.Web.csproj
```

> [!TIP]
> Server adresini konsol çıktısından doğrulayın. `Vortex:ServerBaseUrl` için eski veya tahmini port kullanmayın.

## Repository doğrulaması

Kuruluma başlamadan önce doğru repository kökünde olduğunuzu doğrulayın:

```powershell
# [SALT-OKUNUR]
Get-ChildItem
Test-Path .\VortexAI.Public.sln
Test-Path .\Vortex.Server.Public\Vortex.Server.Public.csproj
Test-Path .\Vortex.Web\Vortex.Web.csproj
Test-Path .\Vortex.Public.Tests\Vortex.Public.Tests.csproj
```

Beklenen tüm `Test-Path` sonuçları `True` olmalıdır.

Yanlış klasördeyseniz restore veya run komutları proje bulunamadı hatası verir. Komutları repository kökünden çalıştırın.

## Server yapılandırması

Kanonik örnek dosya:

```text
Vortex.Server.Public/appsettings.example.json
```

Gerekli ayarlar:

| Ayar | Zorunluluk | Açıklama |
| --- | --- | --- |
| `Jwt:Issuer` | Zorunlu | Yerel issuer tanımlayıcısı |
| `Jwt:Audience` | Zorunlu | Public client audience tanımlayıcısı |
| `Jwt:SigningKey` | Zorunlu | En az 32 UTF-8 byte içeren gizli imzalama anahtarı |
| `Vortex:DataDirectory` | Zorunlu | SQLite verisinin tutulacağı yerel yazılabilir dizin |

### Yapılandırma yöntemleri

Geliştirme ortamında değerler aşağıdaki yöntemlerden biriyle sağlanabilir:

1. commit edilmeyen yerel `appsettings.json`,
2. ortam değişkenleri,
3. destekleniyorsa yerel secret store.

> [!WARNING]
> Örnek dosyayı gerçek secret ile değiştirip commit etmeyin. Örnek dosya secret-free kalmalıdır.

### Placeholder örneği

```json
{
  "Jwt": {
    "Issuer": "<LOCAL_ISSUER>",
    "Audience": "<LOCAL_AUDIENCE>",
    "SigningKey": "<LOCAL_SECRET_WITH_AT_LEAST_32_UTF8_BYTES>"
  },
  "Vortex": {
    "DataDirectory": "<LOCAL_WRITABLE_DATA_DIRECTORY>"
  }
}
```

## Web yapılandırması

Kanonik örnek dosya:

```text
Vortex.Web/appsettings.example.json
```

Temel ayar:

| Ayar | Açıklama |
| --- | --- |
| `Vortex:ServerBaseUrl` | Çalışan yerel `Vortex.Server.Public` adresi |

Örnek placeholder:

```json
{
  "Vortex": {
    "ServerBaseUrl": "https://example.invalid"
  }
}
```

`example.invalid` yalnız inert dokümantasyon adresidir. Gerçek yerel çalıştırmada Server konsolunun gösterdiği adres kullanılmalıdır.

### Web oturum sınırı

Kaynak dokümana göre Web arayüzü:

- tarayıcıdan API anahtarı istemez,
- giriş sonucunda server tarafından sağlanan access token'ı HTTP-only oturum çerezinde tutar,
- public server'a bearer header ile istek yapar.

Bu nedenle access token'ın JavaScript üzerinden görüntülenebilir alana veya dokümantasyona yazılması beklenmez.

## Ortam değişkenleri

ASP.NET Core ortam değişkenlerinde `:` karakteri yerine `__` kullanılır.

| Yapılandırma anahtarı | Ortam değişkeni |
| --- | --- |
| `Jwt:Issuer` | `Jwt__Issuer` |
| `Jwt:Audience` | `Jwt__Audience` |
| `Jwt:SigningKey` | `Jwt__SigningKey` |
| `Vortex:DataDirectory` | `Vortex__DataDirectory` |
| `Vortex:ServerBaseUrl` | `Vortex__ServerBaseUrl` |

### Geçici PowerShell oturumu örneği

```powershell
# [ÖZEL DEĞER GEREKİR]
# Değerleri yalnız geçerli terminal oturumuna uygular.

$env:Jwt__Issuer = "<LOCAL_ISSUER>"
$env:Jwt__Audience = "<LOCAL_AUDIENCE>"
$env:Jwt__SigningKey = "<LOCAL_SECRET_WITH_AT_LEAST_32_UTF8_BYTES>"
$env:Vortex__DataDirectory = "<LOCAL_WRITABLE_DATA_DIRECTORY>"
```

> [!CAUTION]
> Gerçek secret'ı ekran görüntüsüne, PowerShell transcript'ine, issue'ya veya sohbet mesajına yazmayın.

### Değerlerin varlığını secret yazdırmadan kontrol etme

```powershell
# [SALT-OKUNUR]
if ([string]::IsNullOrWhiteSpace($env:Jwt__SigningKey)) {
    "Jwt__SigningKey ayarlanmamış"
} else {
    "Jwt__SigningKey ayarlanmış; değer gösterilmiyor"
}
```

## Yerel veri dizini

`Vortex:DataDirectory`, SQLite verisinin tutulacağı yazılabilir dizini gösterir.

Örnek geliştirme düzeni:

```text
<LOCAL_DEVELOPMENT_ROOT>/
└─ vortex-data/
   └─ public-server/
```

Dizini oluşturma:

```powershell
# [RUNTIME DEĞİŞTİRİR]
New-Item -ItemType Directory -Force -Path "<LOCAL_WRITABLE_DATA_DIRECTORY>"
```

Dizinin yazılabilirliğini doğrulama:

```powershell
# [GEÇİCİ DOĞRULAMA]
$probe = Join-Path "<LOCAL_WRITABLE_DATA_DIRECTORY>" ".write-test"
"ok" | Set-Content $probe
Remove-Item $probe
```

> [!WARNING]
> SQLite dosyasını repository içine yerleştirmeyin. Veri dizini `.gitignore` kapsamı dışında kalıyorsa commit öncesi `git status` kontrolü yapın.

## Public Server'ı çalıştırma

```powershell
# [RUNTIME DEĞİŞTİRİR]
dotnet run --project Vortex.Server.Public/Vortex.Server.Public.csproj
```

Başlatma sonrası:

- konsolda dinlenen URL'yi kaydedin,
- yapılandırma veya signing key hatası olup olmadığını kontrol edin,
- veri dizininin yazılabilir olduğunu doğrulayın,
- hata çıktısında secret görünmediğinden emin olun.

### Release yapılandırmasıyla çalıştırma

```powershell
# [RUNTIME DEĞİŞTİRİR]
dotnet run --project Vortex.Server.Public/Vortex.Server.Public.csproj -c Release
```

## Web arayüzünü çalıştırma

Web yapılandırmasındaki `Vortex:ServerBaseUrl`, aktif public server adresiyle eşleşmelidir.

```powershell
# [RUNTIME DEĞİŞTİRİR]
dotnet run --project Vortex.Web/Vortex.Web.csproj
```

Başlatma sonrası:

1. Web konsolundaki URL'yi tarayıcıda açın.
2. Server bağlantı hatası varsa önce Server'ın çalıştığını doğrulayın.
3. Web'in yapılandırdığı base URL ile Server konsol adresini karşılaştırın.
4. Tarayıcıdan API key beklenmediğini doğrulayın.

## Restore, build ve test

### 1. Restore

```powershell
# [RUNTIME DEĞİŞTİRİR — NuGet cache/obj]
dotnet restore VortexAI.Public.sln
```

### 2. Release build

```powershell
# [RUNTIME DEĞİŞTİRİR — bin/obj]
dotnet build VortexAI.Public.sln -c Release --no-restore
```

### 3. Public testler

```powershell
# [RUNTIME DEĞİŞTİRİR — test output]
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

### Temiz doğrulama

```powershell
# [DEĞİŞTİRİR]
dotnet clean VortexAI.Public.sln -c Release
dotnet restore VortexAI.Public.sln
dotnet build VortexAI.Public.sln -c Release --no-restore
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

> [!NOTE]
> `dotnet clean` build çıktılarını siler; kaynak dosyaları veya yerel veri dizinini silmez. Yine de çalışma alanı değişikliklerini `git status` ile kontrol edin.

## Çalışma zamanı doğrulaması

### Katmanlı kontrol

1. .NET SDK kullanılabilir.
2. Solution restore edilir.
3. Release build geçer.
4. Public testler geçer.
5. Server yapılandırması yüklenir.
6. SQLite data directory yazılabilir.
7. Server başarılı başlar.
8. Web doğru base URL ile başlar.
9. Kullanıcı giriş ve cihaz akışları owner isolation sınırlarını korur.

### Process kontrolü

```powershell
# [SALT-OKUNUR]
Get-Process dotnet -ErrorAction SilentlyContinue
```

### Port/URL kontrolü

Kullanılacak kesin URL'yi konsol çıktısından alın. Port numarasını eski belge veya ekran görüntüsünden tahmin etmeyin.

```powershell
# [SALT-OKUNUR]
Invoke-WebRequest -Method Head -Uri "<ACTIVE_LOCAL_SERVER_URL>" -UseBasicParsing
```

Bu kontrol endpoint davranışına göre `HEAD` isteğini desteklemeyebilir. Böyle bir durumda uygun public health veya ana endpointi kullanın; endpoint adı kaynak koddan doğrulanmalıdır.

## Güncelleme akışı

Yerel repository güncellemesinde önerilen sıra:

```powershell
# [SALT-OKUNUR]
git status --short
git branch --show-current

# [DEĞİŞTİRİR]
git pull --ff-only

dotnet restore VortexAI.Public.sln
dotnet build VortexAI.Public.sln -c Release --no-restore
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

> [!WARNING]
> Yerel değişiklikler varken `git pull`, dosya çakışmalarına yol açabilir. Önce `git status` çıktısını inceleyin; kullanıcı değişikliklerini silmeyin.

## Sorun giderme

| Belirti | Olası neden | Güvenli kontrol | Çözüm yönü |
| --- | --- | --- | --- |
| `dotnet` bulunamadı | .NET SDK kurulu değil veya PATH güncel değil | `dotnet --info` | .NET 8 SDK'yı kurun; terminali yeniden açın |
| Solution bulunamadı | Yanlış çalışma dizini | `Test-Path .\VortexAI.Public.sln` | Repository köküne geçin |
| Restore başarısız | Ağ/NuGet kaynağı veya proje referansı | Restore çıktısındaki ilk hatayı inceleyin | İlk kesin hatayı çözün; `obj` silmeyi ilk adım yapmayın |
| Signing key hatası | Değer eksik veya 32 byte'tan kısa | Değerin varlığını yazdırmadan kontrol edin | Güvenli yerel secret sağlayın |
| SQLite açılamıyor | Data directory yok veya yazılamıyor | Yazma probe'u çalıştırın | Dizini oluşturun ve izinleri düzeltin |
| Web Server'a bağlanamıyor | Yanlış `ServerBaseUrl` veya Server kapalı | İki uygulamanın konsol URL'lerini karşılaştırın | Aktif URL'yi yapılandırın |
| Test build bulamıyor | `--no-build` kullanılırken build yapılmadı | `bin/Release` ve önceki build sonucunu kontrol edin | Önce Release build çalıştırın |
| Git değişiklik gösteriyor | Yerel config/data yanlış konumda | `git status --short` | Secret/data dosyasını repo dışına taşıyın ve ignore politikasını düzeltin |
| Port kullanımda | Başka süreç aynı portu dinliyor | Konsol hatası ve çalışan process'ler | Süreci bilinçli sonlandırın veya yerel port yapılandırmasını değiştirin |

### İlk hata sınırı

Sorun giderirken en son görünen hata yerine **ilk kesin hata** sınırını bulun:

1. Komutu yeniden çalıştırın.
2. Çıktının ilk `error`, `fail` veya exception bölümünü bulun.
3. Secret içerebilecek satırları paylaşmadan önce redakte edin.
4. Hatanın restore, build, config, data, runtime veya ağ katmanında olduğunu sınıflandırın.
5. Yalnız ilgili katmanı düzeltin.

## Güvenlik kontrol listesi

### Commit öncesi

```powershell
# [SALT-OKUNUR]
git status --short
git diff --check
git diff --cached --check
```

- [ ] Gerçek `appsettings.json` staged değil.
- [ ] `.env` veya secret dosyası staged değil.
- [ ] SQLite/veritabanı dosyası staged değil.
- [ ] Log veya build çıktısı staged değil.
- [ ] Görsellerde parola, token veya kullanıcı verisi yok.
- [ ] Örnek URL'ler inert placeholder.
- [ ] Doküman bağlantıları göreli ve doğru.

### Çalıştırma öncesi

- [ ] .NET 8 SDK doğrulandı.
- [ ] Repository kökü doğrulandı.
- [ ] Signing key güvenli yerel kaynaktan sağlandı.
- [ ] Data directory repo dışında ve yazılabilir.
- [ ] Web base URL aktif Server adresine ayarlandı.

### Yayın öncesi

- [ ] `dotnet restore` geçti.
- [ ] Release build geçti.
- [ ] Public testler geçti.
- [ ] Sonuç belirli commit/revision için kaydedildi.
- [ ] Public export secret/data/runtime-state taramasından geçti.

## Kurulum görünümü

![VORTEX kurulum görünümü](../images/setup/vortex-setup-overview.png)

Bu ekran görüntüsü VORTEX'in kurulum ve ilk yapılandırma deneyimini belgelemek için ayrılmıştır. Public belgede kullanılan görseller:

- gerçek kullanıcı verisi,
- parola,
- token,
- API anahtarı,
- üretim adresi

göstermemelidir.

## İlgili belgeler

| Belge | Açıklama |
| --- | --- |
| [Ana README](../README.md) | Proje tanıtımı, hızlı kurulum ve doküman kartları |
| [Dokümantasyon Merkezi](README.md) | Tüm public belgeler |
| [Public Sistem Mimarisi](ARCHITECTURE.md) | Güvenlik, owner isolation ve device-job akışı |
| [Takım ve TEKNOFEST](TEAM.md) | Ekip hikâyesi ve roller |
| [Destekçiler](SUPPORTERS.md) | Kurumsal katkılar ve teşekkür |

---

[← Ana sayfaya dön](../README.md)
