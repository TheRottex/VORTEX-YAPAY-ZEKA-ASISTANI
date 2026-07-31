# Vortex.Shared

`Vortex.Shared`, Vortex bileşenleri arasında paylaşılan .NET 8 sözleşmelerini ve güvenlik yardımcılarını içerir.

## İçerik

- Worker heartbeat, job claim ve completion DTO'ları
- Agent job, owner scope ve cihaz işlemi sözleşmeleri
- Kimlik doğrulama/rol/veri doğrulama modelleri
- HMAC imzalama ve canonical request yardımcıları
- Güvenli göreli yol / workspace path doğrulama yardımcıları

Ana kaynak dosyası: [`Class1.cs`](Class1.cs). Proje dosyası: [`Vortex.Shared.csproj`](Vortex.Shared.csproj).

## Kullanım sınırı

Bu klasör kaynak sözleşme referansıdır; runtime yayın çıktısı değildir. `bin/`, `obj/`, PDB/DLL, kullanıcı verisi, veritabanı, token, gerçek environment dosyası ve deployment state içermez.

Worker ile ilişkili sözleşmeler için [`../Vortex.HermesWorker/README.md`](../Vortex.HermesWorker/README.md) dosyasına bakın. Private Worker operasyonları gerçek secret, seed, Docker runtime state veya gerçek model-router bağlantı bilgisi olmadan anlatılır.
