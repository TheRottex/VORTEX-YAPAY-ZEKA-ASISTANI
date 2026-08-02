# Vortex.LocalHermesWorker

> [!WARNING]
> **Vortex.LocalHermesWorker güncel destek durumu: GELİŞTİRME AŞAMASINDA**
>
> Hermes Worker daha önce geliştiriciye ait özel laptop, WSL ortamı, private Worker kimliği, seed dosyaları ve model yönlendiricisiyle çalıştırılmıştır. Programcı olmayan genel son kullanıcı kurulumu önceki sürümlerde bulunmuyordu.
>
> `Vortex.LocalHermesWorker`, bu eksikliği kapatmak için geliştirilen kurulum ve yönetim katmanıdır. Ayrı Worker motoru değildir; `Vortex.HermesWorker`, Docker image yapısı, HMAC güvenliği, Agent Job Queue, seed koruması ve workspace izolasyonunu kullanır.
>
> Bu klasörde gerçekten bulunan manager şu anda yalnız güvenli ön kontrol ve mevcut servis yaşam döngüsü komutlarını sunar. Pairing, otomatik kurulum, update, rollback, revoke ve uninstall komutları henüz güvenli otomasyon olarak tamamlanmadı; hazırmış gibi kullanılmamalıdır.
>
> Merkezi Worker sürekli açık değildir. Yerel Worker kapalıyken Hermes dışındaki VORTEX özellikleri çalışmaya devam etmelidir.
>
> [GÖRSEL EKLENECEK: Vortex.LocalHermesWorker kurulum ve çalışma akışı]

## Başlangıç

Windows PowerShell içinde proje kökünde çalıştırın:

```powershell
dotnet run --project Vortex.LocalHermesWorker -- preflight
```

`preflight`, WSL durumu, WSL dağıtımları ve Docker daemon erişimini kontrol eder. Kalıcı dosya, servis veya secret oluşturmaz.

## Mevcut yönetim komutları

Aşağıdaki komutlar yalnız önceden güvenli biçimde kurulmuş `vortex-hermes-worker.service` için kullanılabilir:

```powershell
dotnet run --project Vortex.LocalHermesWorker -- status
dotnet run --project Vortex.LocalHermesWorker -- start
dotnet run --project Vortex.LocalHermesWorker -- stop
dotnet run --project Vortex.LocalHermesWorker -- restart
dotnet run --project Vortex.LocalHermesWorker -- logs
```

`start`, `stop` ve `restart` WSL user service runtime durumunu değiştirir. `logs` private içerik taşıyabileceğinden çıktısını paylaşmadan önce gözden geçirin.

## Henüz tamamlanmayan akışlar

`pair`, `install`, `test`, `update`, `rollback`, `revoke` ve `uninstall` adları manager yardımında görünür; güvenli otomasyonları henüz yoktur. Bu nedenle kullanıcıdan token, seed, provider anahtarı veya systemd dosyası elle istemeyen desteklenmiş kurulum akışı henüz yayınlanmamıştır.

Tamamlandığında manager; tek kullanımlık pairing code, kullanıcıya ait Worker kaydı, tokenın güvenli saklanması, OpenAI-compatible provider yapılandırması, Docker image hazırlığı, systemd kurulumu, heartbeat, `E2E_OK`, update/rollback ve veri korumalı uninstall işlemlerini yönetecektir.

[GÖRSEL EKLENECEK: Vortex.LocalHermesWorker kurulum başlangıç ekranı]
[GÖRSEL EKLENECEK: Sistem gereksinimi kontrol sonucu]
[GÖRSEL EKLENECEK: VORTEX hesabıyla Worker eşleştirme]
[GÖRSEL EKLENECEK: Provider veya router yapılandırması]
[GÖRSEL EKLENECEK: Başarılı Worker heartbeat]
[GÖRSEL EKLENECEK: E2E_OK test sonucu]
[GÖRSEL EKLENECEK: Başlatma, durdurma ve durum yönetimi]
[DOSYA YOLU EKLENECEK: Kurulum ekran görüntüsü]
[BAĞLANTI EKLENECEK: Yerel kurulum videosu]

## Güvenlik sınırı

Gerçek Worker tokenı, provider API anahtarı, seed içeriği, kullanıcı workspace verisi ve private loglar bu repository’ye veya komut çıktısına eklenmez. Worker yalnız Docker modunda çalışmalıdır; root container, otomatik `chown`, process-mode fallback ve TLS doğrulamasını kapatma desteklenmez.

Teknik Worker işletimi için [Vortex.HermesWorker teknik rehberi](../Vortex.HermesWorker/README.md) belgesine bakın.
