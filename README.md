# Geçit

PC'yi açar açmaz açılan, interneti ve bilgisayarı en iyi şekilde kullandıran hepsi-bir-arada Windows uygulaması.

| Özellik | Durum |
|---|---|
| **Engel Aşma**: GoodbyeDPI-Turkey ile DPI + DNS engellerini VPN'siz aşar | ✅ v0.1 |
| **Akıllı DNS**: sağlayıcı hız ölçümü, DNS engeli teşhisi, Hızlı DNS ya da şifreli DNS (DoH) | ✅ v0.2 |
| **Gizli Tarayıcı**: iz bırakmayan, reklam engelleyicili sekmeli tarayıcı | yakında |
| **Oyun Modu**: arka plan uygulamalarını dondurur, güç planı, öncelik | yakında |
| **İnternet**: canlı ping/jitter, hız testi, uygulama başına trafik | yakında |

## Derleme

Gereksinimler: .NET 10 SDK, WebView2 Runtime (Windows 10/11'de genelde kurulu).

```powershell
dotnet build
.\src\Gecit\bin\Debug\net10.0-windows\win-x64\Gecit.exe
```

## Nasıl çalışır

```
Gecit.exe (normal kullanıcı)            Gecit.exe --elevated install <yöntem>   (yalnızca kurulumda, UAC)
 ├─ WinForms penceresi + WebView2        ├─ dpi\ → C:\Program Files\Gecit\dpi\
 │   └─ wwwroot/ (HTML/CSS/JS arayüz)    ├─ sc create GecitDPI  (start=demand)
 ├─ sistem tepsisi simgesi               └─ sc sdset: etkileşimli kullanıcıya yalnızca başlat/durdur izni
 └─ GecitDPI hizmetini başlat/durdur
        └─ goodbyedpi.exe → WinDivert64.sys
```

- Uygulama **yönetici olmadan** çalışır. Yönetici izni yalnızca hizmet kurulurken, yöntem değişirken ve kaldırılırken istenir.
- Hizmet SYSTEM olarak çalıştığı için dosyaları kullanıcının değiştiremeyeceği `Program Files` altına kopyalanır. Kullanıcılara hizmetin yapılandırmasını değiştirme izni **verilmez**, yalnızca başlatma ve durdurma izni verilir; aksi halde yetki yükseltme açığı olurdu.
- "Açık" durumu, hizmet çalışıyor **ve** WinDivert sürücüsü yüklü demektir. goodbyedpi, sürücü açılmadan önce "çalışıyor" bildirir.
- Ayarlar: `%APPDATA%\Gecit\settings.json`. Açılışta başlatma: `HKCU\...\Run\Gecit` (`--tray`).

### Akıllı DNS

| Mod | Ne yapar |
|---|---|
| Otomatik | Modemin/operatörün DNS'i (Geçit'ten önceki ayar geri yüklenir). Engel aşma açıkken GoodbyeDPI'ın Yandex yönlendirmesi DNS engelini aşar. |
| Hızlı DNS | Seçilen sağlayıcının adreslerini internete bağlı bağdaştırıcılara yazar (`netsh`). Arka planda bir şey çalışmaz. |
| Şifreli DNS | `GecitDNS` hizmeti (`src/Gecit.Dns`, LocalService hesabıyla) 127.0.0.1:53 ve [::1]:53'te dinler, sorguları RFC 8484 DoH ile iletir. Sistem DNS'i 127.0.0.1 / ::1 olur. |

- GoodbyeDPI'ın Yandex DNS yönlendirmesi, Akıllı DNS açıkken kapatılır; yoksa kullanıcının seçtiği DNS'i ezerdi. Yöntemler (Standart, TTL'siz, Sağlam, TTL 3) bundan bağımsızdır.
- Güvenlik sırası: sistem DNS'i, DoH hizmeti gerçek bir sorguyu yanıtladıktan **sonra** 127.0.0.1'e çevrilir. Hizmet kaldırılmadan **önce** de başka adrese çevrilir. Böylece DNS'siz kalınan bir an olmaz.
- DoH yedekleri: seçilen sağlayıcı → Cloudflare/Google/Quad9 → son çare düz UDP. DoH tamamen çökerse gizlilik azalır ama internet kesilmez. Hizmet çökerse Windows 3 kez yeniden başlatır, uygulama da ayrıca başlatmayı dener (kullanıcılar başlatabilir ama durduramaz).
- İlk değişiklikten önceki DNS ayarları `C:\Program Files\Gecit\dns-backup.json` dosyasına yedeklenir. Otomatik moda dönünce ve "Geçit'i sistemden temizle" ile geri yüklenir.
- Test: `Gecit.Dns.exe cloudflare --console --port 5454` (5353 mDNS'e ait, kullanmayın). Tek sorgu için: `Gecit.Dns.exe quad9 --query discord.com`. Not: Windows `nslookup` `-port=` seçeneğini yok sayıp 53'e sorar.

Bu bağlantıda (Türk Telekom, 2026-10-26) ölçülenler: operatör engelli adları 195.175.254.2'ye çeviriyor, ama başka sunuculara giden 53 portu trafiğine karışmıyor. Yani Hızlı DNS de DNS engelini aşar. DoH sorgu süreleri: Google ~54 ms, AdGuard ~56, Cloudflare ~61, Quad9 ~64, Yandex ~113.

## Üçüncü taraf dosyalar

`third_party/goodbyedpi/`, [GoodbyeDPI-Turkey](https://github.com/cagritaskn/GoodbyeDPI-Turkey) `release-0.2.3rc3-turkey` sürümünün x86_64 dosyalarıdır. 2026-10-26'da doğrulandı:

| Dosya | SHA-256 | Not |
|---|---|---|
| goodbyedpi.exe | `8d412b09…c430dd1f` | Importları yalnızca WinDivert, CryptGenRandom, servis API'si, CRT, ws2_32. Ağ istemcisi, süreç başlatma ya da kayıt defteri yazma yok. Stringler 0.2.3rc3 kaynağıyla birebir aynı. |
| WinDivert.dll | `6110bfa4…5e6c7e` | Resmî WinDivert 2.2.0-C ile birebir aynı. |
| WinDivert64.sys | `e69b5ba3…e82cf50` | 2.2.0-D sürümü, Cloudveil Technology imzası geçerli. |

WinDivert 2.2.2 Windows Defender tarafından `Misleading:Win32/Malgent!AMTB` olarak işaretleniyor, 2.2.0 işaretlenmiyor. Bu yüzden 2.2.0 kullanılıyor.

Lisanslar `third_party/goodbyedpi/licenses/` içinde: GoodbyeDPI Apache 2.0, WinDivert LGPLv3.

## Uyarı

Geçit bir VPN değildir. Kimliğini gizlemez, sadece operatörün DPI ve DNS engellerini aşar. Kullanımdan doğan sorumluluk kullanıcıya aittir.
