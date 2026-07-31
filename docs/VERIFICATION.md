# Doğrulama Kayıtları

## Mevcut Doğrulama Bulguları

Bu belge, public kaynak kodu sürümünün doğrulama sürecinde elde edilen kanıtları ve doğrulama sonuçlarını kayıt altına alır.

Bu kayıtlar yalnızca incelenen sürüm için geçerlidir.

---

## Public Kaynak Kodu Doğrulaması

Public kaynak kodu doğrulaması;

- Çalıştırılan komutların çıktıları,
- Test sonuçları,
- Güvenlik taramaları,
- Repository hijyen kontrolleri

ile birlikte kayıt altına alınmalıdır.

---

## Görev Tamamlama Doğrulamaları

Public test sunucusu aşağıdaki HTTP durumlarını doğrulamaktadır.

| Senaryo | HTTP Durumu |
|---------|-------------|
| Geçersiz cihaz kimliği | 401 Unauthorized |
| Bulunamayan görev | 404 Not Found |
| Bekleyen görev | 404 Not Found |
| Başka cihaza ait görev | 404 Not Found |
| Başarıyla tamamlanan görev | 200 OK |
| Tekrarlanan tamamlama isteği | 200 OK |
| Yaşam döngüsü koruma hatası | 409 Conflict |

---

## Doğrulanan Davranışlar

Görev tamamlama testleri aşağıdaki özellikleri doğrulamaktadır.

- Cihaz sahibi izolasyonu
- Tekrarlanan isteklerde aynı verinin korunması
- DryRun değerinin değiştirilememesi
- Sunucu tarafından yönetilen görev yaşam döngüsü

---

## Rate Limiting Doğrulamaları

Her test için yeni bir public sunucu örneği kullanılmaktadır.

Doğrulanan davranışlar:

- Korunan endpoint'lerde ilk 10 isteğin kabul edilmesi
- 11. isteğin Generic HTTP 429 döndürmesi
- Gerçek görev kuyruğu oluşturulabilmesi
- Login limitinin Job Claim işlemini etkilememesi
- X-Forwarded-For başlığı kullanılarak limitin aşılamaması

---

## Repository Hijyen Kontrolü

RepositoryHygieneTests aşağıdaki doğrulamaları gerçekleştirir.

- git ls-files çıktısını okur.
- PUBLIC_EXPORT_MANIFEST.json ile birebir karşılaştırır.
- İki yönlü farklılıkları raporlar.
- Git index üzerinde hiçbir değişiklik yapmaz.
- Bootstrap aşamasında eksik dosyalar nedeniyle başarısız olması beklenir.

Ayrıca aşağıdaki içeriklerin public sürüme dahil edilmediğini doğrular.

- Operasyon dosyaları
- Build çıktıları
- Binary dosyalar
- Checksum dosyaları
- Private depolama içerikleri
- Manuel yüklenen dosyalar

Release klasöründe yalnızca

```
Release/v1.0.1.md
```

dosyasına izin verilir.

---

## Desteklenmeyen Doğrulamalar

Bu belge aşağıdaki konularda herhangi bir doğrulama iddiasında bulunmaz.

- Private ürün kaynak kodu
- Üretim ortamı dağıtımı
- Kurulum paketleri
- GitHub Release Assets
- OAuth yapılandırmaları
- Donanım doğrulamaları
- Sesli asistan
- Uzak cihaz çalıştırma

---

# Public Yayın Öncesi Kontrol

Public sürüm yayınlanmadan önce aşağıdaki komutlar çalıştırılmalıdır.

```powershell
dotnet restore VortexAI.Public.sln

dotnet build VortexAI.Public.sln -c Release --no-restore

dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

Daha sonra;

- `git ls-files`
- `docs/PUBLIC_EXPORT_MANIFEST.json`

iki yönlü olarak karşılaştırılmalıdır.

Ayrıca repository içerisinde aşağıdaki içeriklerin bulunmadığı doğrulanmalıdır.

- API anahtarları
- Gizli bilgiler
- Private anahtarlar
- Runtime yapılandırmaları
- Arşiv dosyaları
- Veritabanları
- Build çıktıları
- Worker
- Hermes
- Tailscale
- Deployment bileşenleri

Repository, tüm doğrulamalar başarıyla tamamlanmadan ve sonuçlar kayıt altına alınmadan **yayına hazır** olarak işaretlenmemelidir.