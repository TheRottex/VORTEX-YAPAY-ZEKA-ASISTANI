# Public Sistem Mimarisi

```text
Kimliği Doğrulanmış Public Kullanıcı                 Uyumlu Cihaz İstemcisi
                  |                                             |
                  |                                             |
                  +------------------------+--------------------+
                                           |
                                           v
                              Vortex.Server.Public
                    Kimlik Doğrulama • Yetkilendirme
                    SQLite Veri Katmanı
                    Sahip (Owner) Bazlı Görev Yönetimi
```

---

# Genel Mimari

Public sürümde tüm istemci istekleri **Vortex.Server.Public** bileşeni üzerinden yönetilir.

Sunucu aşağıdaki temel görevlerden sorumludur:

- Kullanıcı kaydı
- Kullanıcı girişi
- JWT Bearer Token doğrulaması
- Profil bilgisi sorgulama
- Cihaz kaydı
- Cihaz listeleme
- Güvenli görev planlama
- Görev kuyruğu oluşturma
- Görev talep etme (Claim)
- Görev tamamlama
- Görev durumunun görüntülenmesi

Tüm veriler SQLite üzerinde parametreli sorgular kullanılarak güvenli şekilde saklanmaktadır.

---

# Kimlik Doğrulama

Kimlik doğrulama işlemleri **Bearer Token** yapısı kullanılarak gerçekleştirilir.

Token doğrulaması `TokenService` tarafından yapılır.

Her cihaz yalnızca:

- kendisine ait,
- iptal edilmemiş,
- geçerli

Device Token ile kimlik doğrulayabilir.

Başka bir cihaza ait görevlere erişim mümkün değildir.

---

# Görev Tamamlama Süreci

Bir görev tamamlanırken istemciden yalnızca aşağıdaki bilgiler kabul edilir.

- DeviceId
- DeviceToken
- Success
- Code
- Message
- Timeline

Aşağıdaki bilgiler istemciden kabul edilmez:

- DryRun
- Komutlar
- Terminal çıktıları
- Teknik ayrıntılar
- Sunucu tarafından yönetilen alanlar

---

# HTTP Durumları

Sunucu doğrulama sonrasında aşağıdaki durum kodlarını döndürür.

| Durum | HTTP |
|-------|------|
| Geçersiz veya iptal edilmiş cihaz | 401 Unauthorized |
| Görev bulunamadı | 404 Not Found |
| Başka cihaza ait görev | 404 Not Found |
| Bekleyen görev | 404 Not Found |
| Başarıyla tamamlandı | 200 OK |
| Aynı görevin tekrar tamamlanması | 200 OK |
| Yaşam döngüsü koruma hatası | 409 Conflict |

Tekrarlanan tamamlama isteklerinde mevcut kayıt korunur ve üzerine yazılmaz.

---

# DryRun Politikası

DryRun bilgisi yalnızca sunucu tarafından yönetilir.

İstemci bu değeri:

- değiştiremez,
- silemez,
- üzerine yazamaz.

Görev tamamlandıktan sonra da sunucuda saklanan özgün DryRun değeri korunur.

---

# Yerel İşlem Sınırları

Public sürüm serbest komut çalıştırılmasına izin vermez.

Yalnızca önceden tanımlanmış ve izin verilmiş araçlar görev kuyruğuna eklenebilir.

Ek olarak;

- Parametre uzunlukları sınırlandırılır.
- Riskli işlemler kullanıcı onayı gerektirir.
- Onay gerektiren işlemler onay verilmeden kuyruğa eklenemez.

---

# Public Repository Yapısı

Repository yalnızca açık kaynak olarak paylaşılmasına izin verilen bileşenleri içerir.

`Vortex.Desktop/`

klasörü yalnızca:

- statik örnek servis dosyaları
- görsel varlıklar (orb asset)

barındırmaktadır.

Bu dosyalar:

- Public Solution tarafından derlenmez.
- Desktop çalışma zamanı oluşturmaz.
- LocalAgent görevi görmez.
- Uzak cihaz çalıştırma yeteneği sağlamaz.

---

# Public Sürümde Bulunmayan Bileşenler

Bu mimariye aşağıdaki sistemler dahil değildir.

- Desktop çalışma zamanı
- LocalAgent
- Hermes
- HermesWorker
- Worker servisleri
- Uzak çalıştırma altyapısı
- Tailscale
- Docker
- Deployment sistemleri
- Admin Paneli
- Private Web kaynak kodları
- Üretim ortamı servisleri
- Harici servis sağlayıcı entegrasyonları
- Build çıktıları
- Kurulum paketleri

---

# Mimari İlkeleri

Public sürüm aşağıdaki temel prensipler üzerine kurulmuştur.

- Güvenli kimlik doğrulama
- Yetkilendirilmiş cihaz erişimi
- Sahip (Owner) bazlı görev yönetimi
- Sunucu tarafından yönetilen işlem yaşam döngüsü
- Açık kaynak kullanımına uygun sade mimari
- Private bileşenlerden tamamen ayrıştırılmış yapı