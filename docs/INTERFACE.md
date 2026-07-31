# Arayüz Görsel Envanteri

Bu belge, public Vortex deposunda yayınlanması onaylanan arayüz görsellerinin tek envanteridir. Burada listelenmeyen hiçbir ekran görüntüsü, mockup veya çalışma zamanı görüntüsü public yayın kapsamına girmez.

## Yayınlanmış görseller

| Dosya | Kullanım amacı | Önerilen alternatif metin | İnceleme durumu |
| --- | --- | --- | --- |
| `images/interface/vortex-interface-overview.png` | README arayüz genel görünümü | `Vortex koyu temalı arayüz genel görünümü` | İncelendi; parola, token, API anahtarı veya gerçek kullanıcı verisi görülmedi. |
| `images/setup/vortex-setup-overview.png` | README ve kurulum rehberi görseli | `Vortex kurulum ve ilk yapılandırma görünümü` | Public kurulum belgelemesi için ayrıldı; gerçek yapılandırma veya üretim erişim bilgisi içermemelidir. |

## Yayın sınırları

- `images/interface/raw/` altındaki kaynak ekran görüntüleri inceleme materyalidir; README veya başka public belge tarafından doğrudan bağlanmaz.
- Yeni görsel eklenmeden önce gerçek kullanıcı verileri, e-posta adresleri, parolalar, access tokenlar, API anahtarları, endpointler, yerel dosya yolları, cihaz kimlikleri, loglar ve üretim altyapısı ayrıntıları kaldırılmalıdır.
- Görseller, desteklenmeyen Desktop, LocalAgent, Worker, Hermes, Tailscale, Docker, deployment veya private Web çalışma zamanı için işlevsellik ya da dağıtım taahhüdü oluşturmaz.
- Görsel ekleme veya kaldırma, `docs/PUBLIC_EXPORT_MANIFEST.json` içindeki kesin public dosya listesiyle aynı değişiklikte güncellenmelidir.

## İnceleme kaydı

Arayüz genel görünümü için kaynak inceleme ve yayın kararı [çalışma ilerleme kaydında](WORK_PROGRESS.md) tutulur. Public kapsam kuralları için [Public Scope](PUBLIC_SCOPE.md) ve [Security](../SECURITY.md) belgelerine bakın.
