# Vortex AI Assistant — Public Subset

## Status / Durum

This repository is a **public subset under construction**. It is not a complete product release and is **not verified** unless separate build and test evidence is published for a specific revision.

Bu depo, geliştirme aşamasındaki **public subset** içindir. Tam ürün veya yayın değildir. Belirli bir revizyon için ayrı derleme ve test kanıtı yayımlanmadıkça **doğrulanmış kabul edilmez**.

## Public scope / Açık kapsam

Current public source is limited to:

- `Vortex.Server.Public`: a .NET 8 public-server subset.
- `Vortex.Contracts`: shared request, response, role, and device-job contracts.
- SQLite-backed public data access.
- Authentication helpers and owner-scoped public device-job service code.

See [docs/PUBLIC_SCOPE.md](docs/PUBLIC_SCOPE.md) for exclusions and boundaries.

## Architecture / Mimari

The documented public topology is:

```text
Browser → Vortex.Web → Vortex.Server.Public → SQLite
                            ↓
                    owner-scoped device jobs
```

This repository does not document, distribute, or configure private execution infrastructure.

## Arayüz görünümü

![Vortex arayüz görünümü](docs/images/interface/vortex-interface-overview.png)

Bu görünüm, Vortex'in koyu temalı ve odaklı arayüz yaklaşımını temsil eder. Public dokümantasyonda kullanılan ekran görüntüleri, yalnız proje deneyimini anlatmak amacıyla seçilir; kullanıcı verisi, erişim anahtarı veya üretim yapılandırması içermez.

## Hızlı başlangıç

1. Public server için [`Vortex.Server.Public/appsettings.example.json`](Vortex.Server.Public/appsettings.example.json) dosyasını temel alın; gerçek `Jwt:SigningKey` değerini yalnız yerel ortam değişkeni veya gizli depoda tutun.
2. Web arayüzü için [`Vortex.Web/appsettings.example.json`](Vortex.Web/appsettings.example.json) içindeki `Vortex:ServerBaseUrl` değerini çalıştırdığınız public server adresiyle yerelde yapılandırın.
3. Ayrıntılı güvenlik ve yapılandırma sınırları için [Configuration](docs/CONFIGURATION.md) ve [Security](SECURITY.md) belgelerini izleyin.

> Doğrulama komutları revision-specific kanıt gerektirir. Yayın öncesi [Verification record](docs/VERIFICATION.md) içindeki restore, build ve test adımlarını çalıştırın.

## Public layout

The mandated top-level layout is retained for navigation. Only `Vortex.Desktop` contains the ten reviewed, presentation-only Desktop source and asset copies; it is outside every project compilation path in `VortexAI.Public.sln` and does not add a Desktop or LocalAgent runtime.

- [Vortex.Admin](Vortex.Admin/README.md)
- [Vortex.Desktop](Vortex.Desktop/README.md)
- [Vortex.HermesWorker](Vortex.HermesWorker/README.md)
- [Vortex.LocalAgent](Vortex.LocalAgent/README.md)
- [Vortex.Server](Vortex.Server/README.md)
- [Vortex.Shared](Vortex.Shared/README.md)
- [Vortex.Tests](Vortex.Tests/README.md)
- [Vortex.Web](Vortex.Web/README.md)
- [Screenshot documentation](docs/screenshots/README.md)

## VORTEX Takımı ve Teknofest 2026

VORTEX; yazılım alanında teknik yetkinlik kazanmak, gerçek dünya problemlerine sürdürülebilir çözümler üretmek ve Türkiye'nin dijital dönüşümüne katkı sağlamak amacıyla kurulan genç bir yazılım takımıdır.

- **Takım:** VORTEX
- **Odak:** Yerli yazılım, açık kaynak ekosistemi, güvenli kullanıcı deneyimi ve toplumsal fayda
- **Teknofest 2026:** Takım hikâyesi, projeler, görev dağılımı ve public sürüm yaklaşımı için [takım sayfasına](docs/TEAM.md) bakın.

## Documentation

- [Takım ve Teknofest 2026](docs/TEAM.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Configuration](docs/CONFIGURATION.md)
- [Kurulum rehberi](docs/SETUP.md)
- [Security](SECURITY.md)
- [Contributing](CONTRIBUTING.md)
- [Public scope](docs/PUBLIC_SCOPE.md)
- [Review guide](docs/REVIEW_GUIDE.md)
- [Verification record](docs/VERIFICATION.md)

## Görseller ve kurulum

Arayüz ve kurulum görselleri, public veri/gizlilik incelemesinden sonra belge yapısına eklenir. Görsel dağıtım sınırları için [screenshot notlarına](docs/screenshots/README.md) bakın.

## Release policy / Yayın politikası

No release binaries belong in source control. Source documentation is not evidence of a released, deployed, or verified artifact.
