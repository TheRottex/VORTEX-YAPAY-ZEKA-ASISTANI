# Vortex.HermesWorker teknik çalışma kaynağı

> [!IMPORTANT]
> **Bu belge Vortex.HermesWorker’ın teknik çalışma kaynağıdır**
>
> Bu README, public `Vortex.HermesWorker` bileşeninin Server–Worker HMAC sözleşmesini, Docker one-shot çalışma modelini, canonical WSL release yapısını, seed güvenliğini, workspace izolasyonunu, job yaşam döngüsünü ve doğrulanmış E2E hedefini açıklar.
>
> Bilgiler geliştirici laptop/WSL ortamı referans alınarak güncellenir. Gerçek Worker tokenları, provider anahtarları ve private seed içerikleri bu belgede yer almaz.
>
> Bu belge programcı olmayan son kullanıcı kurulum rehberi değildir. Yerel kullanım için [Vortex Local Hermes Worker Kurulum Rehberi](../Vortex.LocalHermesWorker/README.md) belgesine geçin.
>
> Birincil teknik belge: [VORTEX_HERMES_WORKER_README.md](../VORTEX_HERMES_WORKER_README.md). İki belge çelişmemelidir.

Canonical teknik rehberin eş kopyası bu dosyada oluşturulacak kapsamlı senkronizasyon öncesinde, gerçek kaynak ve güvenlik sınırları için birincil belgeyi kullanın.

## Mevcut doğrulanmış kaynak sözleşmesi

- Worker, Server’a outbound HMAC imzalı heartbeat, claim, job heartbeat ve completion istekleri gönderir.
- Çalışma modu yalnız `docker` olmalıdır. Process-mode fallback yoktur.
- Her iş `docker run --rm` ile, host Worker UID:GID değeriyle çalışır.
- Container home: `/vortex/hermes-home`; `HERMES_HOME`, `HOME`, `USERPROFILE` bu dizine yönlendirilir.
- Container root olarak çalışmaz; bind mount üzerinde otomatik `chown` yapılmaz.
- Image build aşamasında `hermes-agent==0.19.0` ve `edge-tts==7.2.7` kurulur.
- Başarı yalnız stdout ile değil, exit code `0` ile kabul edilir.
- Seed kaynakları yalnız `config.yaml` ve `.env` olabilir; kayıp kaynak fail-closed hata üretir; reparse point reddedilir.

[GÖRSEL EKLENECEK: Server–Worker–Docker–Provider zinciri]
[GÖRSEL EKLENECEK: Canonical WSL Worker klasör yapısı]
[GÖRSEL EKLENECEK: Başarılı Worker heartbeat]
[GÖRSEL EKLENECEK: E2E_OK ve exit code 0 doğrulaması]
[GÖRSEL EKLENECEK: Web/Desktop Agent Job akışı]
[DOSYA YOLU EKLENECEK: İlgili ekran görüntüsü]
