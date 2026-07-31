# İnceleme ve Doğrulama Rehberi

Bu belge, Vortex Yapay Zeka Asistanı Public sürümünün yayınlanmadan önce nasıl incelenmesi ve doğrulanması gerektiğini açıklar.

---

# Public Dışa Aktarım Kapsamını Doğrulama

## 1. Public Manifest Dosyasını Doğrulayın

`docs/PUBLIC_EXPORT_MANIFEST.json` dosyasını inceleyin.

Aşağıdaki durumlarda doğrulamayı başarısız kabul edin:

- Aynı dosyanın birden fazla kez listelenmesi
- Kök dizin (rooted) yollar
- Dizin dışına çıkan (`../`) yollar
- Wildcard (`*`) kullanımı
- Yasaklı yollar
- Private dosya yolları

---

## 2. Manifest ile Git Dosyalarını Karşılaştırın

Manifest içerisindeki `includedPaths` listesi ile

```
git ls-files -z
```

çıktısını iki yönlü karşılaştırın.

Doğrulama aşağıdaki durumları raporlamalıdır:

- Manifestte bulunup Git'te olmayan dosyalar
- Git'te bulunup Manifestte olmayan dosyalar

İlk kurulum sırasında (bootstrap) bütün public kaynak dosyaları Git'e eklenene kadar bu kontrolün başarısız olması beklenir.

İnceleme yapan kişi Git durumunu değiştirmemeli, dosya eklememeli veya stage işlemi gerçekleştirmemelidir.

---

## 3. Release İçeriğini Kontrol Edin

Release klasöründe yalnızca aşağıdaki dosya bulunmalıdır:

```
Release/v1.0.1.md
```

Aşağıdaki içerikler kesinlikle bulunmamalıdır:

- Operasyon dosyaları
- Build çıktıları
- Binary dosyalar
- Checksum dosyaları
- Private depolama içerikleri
- Manuel yüklenmiş dosyalar
- Desktop
- LocalAgent
- Worker
- Hermes
- Tailscale
- Docker
- Deployment
- Admin
- Private Web
- Runtime yapılandırmaları
- Gizli bilgiler
- Veritabanları
- Log dosyaları
- Arşiv dosyaları

---

## 4. Proje Referanslarını Kontrol Edin

Hiçbir proje referansı repository dışındaki bir dizine işaret etmemelidir.

---

# Güvenlik Doğrulamaları

Aşağıdaki güvenlik özelliklerini doğrulayın.

## Kimlik Doğrulama

- JWT imzalama anahtarı en az 32 byte olmalıdır.
- Geçersiz JWT reddedilmelidir.
- Yanlış imza reddedilmelidir.
- Süresi dolmuş Token reddedilmelidir.
- Yanlış Issuer reddedilmelidir.
- Yanlış Audience reddedilmelidir.

---

## Parola Güvenliği

Parolalar

- PBKDF2-SHA256
- Rastgele Salt
- Sabit süreli karşılaştırma

kullanılarak doğrulanmalıdır.

---

## Device Token Güvenliği

Device Token'lar

- Salt ile hashlenmeli
- Sabit süreli karşılaştırma kullanılmalıdır.

---

## Görev Kuyruğu

Görev kuyruğu aşağıdaki kuralları sağlamalıdır.

- Cihaz sahibine ait olmalıdır.
- Yalnızca izin verilen araçlar çalıştırılabilir.
- Parametre sınırları uygulanmalıdır.
- Onay gerektiren işlemler kullanıcı onayı olmadan kuyruğa alınmamalıdır.

---

## Görev Tamamlama

Tamamlama isteği yalnızca aşağıdaki alanları içermelidir.

- DeviceId
- DeviceToken
- Success
- Code
- Message
- Timeline

Aşağıdaki alanlar istemci tarafından gönderilemez.

- DryRun
- Command
- Output
- Teknik detaylar

---

## HTTP Durum Kodları

Doğrulanması gereken davranışlar:

| Durum | HTTP |
|-------|------|
| Geçersiz Device | 401 |
| Bekleyen veya bulunamayan görev | 404 |
| Başarılı tamamlama | 200 |
| Tekrarlanan tamamlama | 200 |
| Diğer durum hataları | 409 |

---

## Rate Limiting

Her endpoint ayrı hız sınırına sahip olmalıdır.

Kontroller:

- IP adresi yalnızca `RemoteIpAddress` üzerinden alınmalıdır.
- Forwarded Header kullanılmamalıdır.
- Kuyruk uzunluğu sıfır olmalıdır.
- Limit aşımında Generic HTTP 429 dönmelidir.
- X-Forwarded-For ile limit aşılmamalıdır.

---

# Yerel Doğrulama

```powershell
dotnet restore VortexAI.Public.sln

dotnet build VortexAI.Public.sln -c Release --no-restore

dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

Repository hijyen testi, public kaynak dosyalarının tamamı Git'e eklenene kadar başarısız olmalıdır.

Bu davranış normaldir.

Test hiçbir zaman Git durumunu değiştirmemeli ve yalnızca mevcut repository durumunu doğrulamalıdır.

Bu doküman yalnızca public kaynak kodunun doğrulanmasını açıklar.

Bir sürümün yayınlandığını, dağıtıldığını veya üretim ortamına alındığını kanıtlamaz.