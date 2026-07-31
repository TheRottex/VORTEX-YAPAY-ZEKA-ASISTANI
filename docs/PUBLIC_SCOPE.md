# Public Sürüm Kapsamı

## Dahil Edilenler

Bu public sürüm aşağıdaki bileşenleri içerir:

- .NET 8 tabanlı public sözleşmeler (Contracts)
- Minimal ASP.NET Core API
- SQLite veritabanı şeması
- Parametreli veri erişim yardımcıları
- PBKDF2-SHA256 parola karmalama altyapısı
- JWT Bearer Token doğrulama sistemi
- AES-GCM şifreleme yardımcıları
- Sahip (Owner) bazlı cihaz kayıt sistemi
- Güvenli cihaz işlem planlama ve kuyruk sistemi
- Yetkilendirilmiş görev talep (Claim) ve tamamlama mekanizması
- Sahibe ait görev durumlarının görüntülenmesi
- Public test projeleri
- Dokümantasyon
- PUBLIC_EXPORT_MANIFEST.json içerisinde belirtilen public dosya listesi

Ayrıca üst dizin yapısı GitHub public sürümü için düzenlenmiştir.

---

## Desteklenen Özellikler

Bu sürüm aşağıdaki yetenekleri desteklemektedir:

- Kullanıcı kayıt ve giriş sistemi
- JWT ile kimlik doğrulama
- Profil görüntüleme
- Cihaz kaydı
- Cihaz yönetimi
- Güvenli görev planlama
- Görev kuyruğu
- Görev talep etme (Claim)
- Görev tamamlama
- DryRun desteği
- IP tabanlı hız sınırlandırma (Rate Limiting)

Görev tamamlama sırasında yalnızca aşağıdaki bilgiler kayıt altına alınır:

- Cihaz bilgileri
- İşlem sonucu
- Durum kodu
- Mesaj
- Zaman çizelgesi

DryRun değeri yalnızca sunucu tarafından yönetilir ve istemci tarafından değiştirilemez.

---

## Dahil Edilmeyenler

Bu public sürüm aşağıdaki bileşenleri içermez:

- Desktop uygulaması
- LocalAgent
- Worker runtime yapılandırmaları, Worker secret'ları ve Worker binary'leri
- Hermes seed dosyaları, provider ayarları ve API anahtarları
- Admin Paneli
- Docker image, volume ve container state'i
- Tailscale
- Gerçek deployment altyapısı
- Private Web kaynak kodları
- Runtime yapılandırmaları
- .env dosyaları
- API anahtarları
- OAuth bilgileri
- JWT gizli anahtarları
- Device Token verileri
- Veritabanları
- Günlük (Log) dosyaları
- Kullanıcı verileri
- Binary dosyalar
- Build çıktıları
- Arşivler
- Özel Git geçmişi

Bu sürüm, yalnız source/reference olarak Hermes Worker kodu ve placeholder-only WSL kurulum rehberi içerir. Gerçek server adresi, IP, token, API anahtarı, runtime environment, Docker image veya kullanıcı verisi içermez. Bu repository tek başına uzak cihaz çalıştırma, Hermes başlatma veya production Worker iletişimi gerçekleştirecek şekilde yapılandırılmamıştır.

---

## Yayın Kuralları

Bu public sürüm yalnızca pozitif dosya listesi mantığı ile hazırlanmıştır.

Yalnızca `docs/PUBLIC_EXPORT_MANIFEST.json` dosyasında belirtilen yollar public sürüme dahildir.

Wildcard kullanılamaz.

Manifest ile `git ls-files` çıktısı birebir eşleşmelidir.

`.gitignore` dosyası tek başına yayın sınırı olarak kabul edilmez.