# Güvenlik Politikası

## Halka açık alt küme sınırı

Bu depo, yapım aşamasında olan ve doğrulanmamış bir halka açık alt kümedir. Depo, dağıtım talimatları, çalışma zamanı yapılandırma değerleri, gizli bilgiler, özel yollar veya sürüm yükleri içermez.

Özel Hermes çalıştırması, İşçi hizmetleri, Tailscale ağ iletişimi, dağıtım sistemleri, gizli bilgiler ve çalışma zamanı yapılandırması bu deponun dışındadır ve sorun bildirimlerine, belgelere, örneklere veya commit’lere eklenmemelidir.

## Güvenlik Açığı Bildirme

Hassas ayrıntıları halka açık bir sorun bildiriminde yayınlamayınız. Proje sorumlusuyla özel bir kanal üzerinden iletişime geçiniz ve aşağıdakileri ekleyiniz:

- etkilenen halka açık dosya ve revizyon;
- sorunu en az adımda yeniden oluşturma adımları;
- etki ve önerilen hafifletme önlemleri;
- yalnızca sansürlenmiş kanıtlar.

Erişim jetonlarını, şifreleri, özel anahtarları, veritabanı dosyalarını, cihaz jetonlarını, üretim URL'lerini veya özel dosya sistemi yollarını eklemeyiniz.

## Güvenlik beklentileri

- Kimlik doğrulama ve yetkilendirmeyi sahip kapsamında tutun.
- Cihaz kimlik bilgilerini gizli tutun; uygun olduğu durumlarda yalnızca korunan veya karma hale getirilmiş biçimlerde saklayın.
- Girişleri doğrulayın ve geçersiz yetkilendirme veya iş durumu durumunda işlemi kapatın.
- Gizli bilgileri veya hassas çalışma zamanı verilerini günlüğe kaydetmeyin.
- Kolaylık için kriptografik kontrolleri, jeton doğrulamasını veya SQLite sahiplik kısıtlamalarını zayıflatmayın.

Güvenlik raporları, bir sürüm zaman çizelgesi taahhüdü olmaksızın önceliklendirilir.

Translated with DeepL.com (free version)