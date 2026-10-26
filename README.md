# Geçit

PC'yi açar açmaz açılan, interneti ve bilgisayarı en iyi şekilde kullandıran hepsi-bir-arada Windows uygulaması.

| Özellik | Durum |
|---|---|
| **Engel Aşma**: GoodbyeDPI-Turkey ile DPI + DNS engellerini VPN'siz aşar | ✅ v0.1 |
| **Akıllı DNS**: sağlayıcı hız ölçümü, DNS engeli teşhisi, Hızlı DNS ya da şifreli DNS (DoH) | ✅ v0.2 |
| **Gizli Tarayıcı**: iz bırakmayan, reklam engelleyicili sekmeli tarayıcı | ✅ v0.3 |
| **Oyun Modu**: arka plan uygulamalarını dondurur, güç planı, öncelik | ✅ v0.4 |
| **İnternet**: canlı ping/jitter, hız testi, uygulama başına trafik | ✅ v0.5 |
| **Ağım**: modeme bağlı cihazlar (üretici, model, ad), yeni cihaz bildirimi | ✅ v0.6 |
| **Açılış**: başlangıç programları ve açılışa etkileri, açılış süresi ölçümü | ✅ v0.6 |
| **Güncelleme**: GitHub'dan yeni sürüm denetimi, uygulama içinden tek tıkla kurulum | ✅ v0.6 |
| **Demolar**: Steam'in yeni, yaklaşan ve popüler demoları; co-op filtresi, takip edilen demo çıkınca bildirim | ✅ v0.7 |
| **Bedava**: Epic ve Steam'de alınca hesapta kalan bedava oyunlar, yeni bedava oyun bildirimi | ✅ v0.7 |

## İndir

[**Releases**](../../releases/latest) sayfasından `Gecit-Setup-<sürüm>.exe` dosyasını indirip çalıştırın. Windows 10 (1809+) veya 11, 64 bit gerekir; .NET kurmaya gerek yoktur.

- Paketin kod imzası olmadığı için Windows "Bilgisayarınız korundu" diyebilir: **Ek bilgi → Yine de çalıştır**.
- Bazı antivirüsler (ör. Kaspersky) GoodbyeDPI'ın kullandığı WinDivert sürücüsünü engelleyebilir. Sürücü imzalı ve açık kaynaklıdır; aşağıda doğrulama notları var.
- Kaldırmak için: Ayarlar → Uygulamalar → Geçit. DNS ayarları eski haline döner, hizmetler ve sürücü silinir.

## Derleme

Gereksinimler: .NET 10 SDK, WebView2 Runtime (Windows 10/11'de genelde kurulu).

```powershell
dotnet build
.\src\Gecit\bin\Debug\net10.0-windows\win-x64\Gecit.exe
```

## Kurulum paketi

```powershell
winget install JRSoftware.InnoSetup   # bir kez
pwsh installer\build.ps1              # -> dist\Gecit-Setup-<sürüm>.exe
```

- `dotnet publish` self-contained çalışır, kullanıcının .NET kurması gerekmez. Paket yaklaşık 38 MB (kurulu hali yaklaşık 120 MB). Sürüm numarası `src\Gecit\Gecit.csproj` içindeki `<Version>` alanından alınır.
- Uygulama `C:\Program Files\Gecit` klasörüne kurulur. Klasör seçilemez, çünkü hizmetler `Gecit.exe`'yi buradan çalıştırır: `GecitDNS` = `Gecit.exe --dns-service <sağlayıcı>`, `GecitDPI` = `dpi\goodbyedpi.exe`. Geliştirme sürümü çalışırken bu klasöre kendini kopyalar (`AppInstall.EnsureInstalledCopy`).
- WebView2 yoksa Microsoft'un bootstrapper'ı sessizce kurulur.
- Güncelleme: kurulum çalışan uygulamayı `Gecit.exe --exit` ile düzgünce kapatır (oyun modu bitirilir, dondurulanlar çözülür). Ardından `sc` ile GecitDNS/GecitDPI hizmetlerini ve WinDivert sürücüsünü durdurur. Dosyalar değiştikten sonra `--elevated resume-services` daha önce çalışanları yeniden başlatır.
- Kaldırma: `--elevated uninstall --full` çalışır. DNS eski haline döner, hizmetler ve sürücü silinir, açılışta başlatma kaydı silinir. Ardından klasör, kısayollar, `%LOCALAPPDATA%\Gecit` ve `%ProgramData%\Gecit` silinir. Kullanıcı ayarları (`%APPDATA%\Gecit`) korunur. 2026-10-26'da sessiz kurulum, kaldırma ve yeniden kurma ile doğrulandı.
- Paketin kod imzası yok, bu yüzden SmartScreen "Bilgisayarınız korundu" uyarısı gösterir. Kurulumdaki bilgilendirme sayfası (`installer\notice-tr.txt`) bunu ve VPN olmadığını açıklar.
- Şifreli DNS hizmetinin günlüğü `%ProgramData%\Gecit\dns.log` dosyasına yazılır. Hizmet doğrulamayı geçemezse son satırlar hata mesajına eklenir.

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
| Şifreli DNS | `GecitDNS` hizmeti (`Gecit.exe --dns-service`, kodu `src/Gecit.Dns`, LocalService hesabıyla) 127.0.0.1:53 ve [::1]:53'te dinler, sorguları RFC 8484 DoH ile iletir. Sistem DNS'i 127.0.0.1 / ::1 olur. |

- GoodbyeDPI'ın Yandex DNS yönlendirmesi, Akıllı DNS açıkken kapatılır; yoksa kullanıcının seçtiği DNS'i ezerdi. Yöntemler (Standart, TTL'siz, Sağlam, TTL 3) bundan bağımsızdır.
- Güvenlik sırası: sistem DNS'i, DoH hizmeti gerçek bir sorguyu yanıtladıktan **sonra** 127.0.0.1'e çevrilir. Hizmet kaldırılmadan **önce** de başka adrese çevrilir. Böylece DNS'siz kalınan bir an olmaz.
- DoH yedekleri: seçilen sağlayıcı → Cloudflare/Google/Quad9 → son çare düz UDP. DoH tamamen çökerse gizlilik azalır ama internet kesilmez. Hizmet çökerse Windows 3 kez yeniden başlatır, uygulama da ayrıca başlatmayı dener (kullanıcılar başlatabilir ama durduramaz).
- İlk değişiklikten önceki DNS ayarları `C:\Program Files\Gecit\dns-backup.json` dosyasına yedeklenir. Otomatik moda dönünce ve "Geçit'i sistemden temizle" ile geri yüklenir.
- Test: `Gecit.Dns.exe cloudflare --console --port 5454` (5353 mDNS'e ait, kullanmayın). Tek sorgu için: `Gecit.Dns.exe quad9 --query discord.com`. Not: Windows `nslookup` `-port=` seçeneğini yok sayıp 53'e sorar.

Bu bağlantıda (Türk Telekom, 2026-10-26) ölçülenler: operatör engelli adları 195.175.254.2'ye çeviriyor, ama başka sunuculara giden 53 portu trafiğine karışmıyor. Yani Hızlı DNS de DNS engelini aşar. DoH sorgu süreleri: Google ~54 ms, AdGuard ~56, Cloudflare ~61, Quad9 ~64, Yandex ~113.

### Gizli Tarayıcı

- `BrowserForm`: üstte HTML araç çubuğu (`wwwroot/browser/chrome.html`), altında her sekme için bir WebView2. Sekmelerin hepsi tek bir **InPrivate** profilini (`gecit-private`) paylaşır. WebView2 bu profilin verisini son sekme kapanınca siler; "Temizle" ayrıca `ClearBrowsingDataAsync` çağırır.
- Reklam/takipçi engeli: Edge izleme koruması "Katı" + `AdBlocker` (StevenBlack unified hosts, yaklaşık 74 bin alan adı, `%LOCALAPPDATA%\Gecit\blocklist-hosts.txt`, haftalık yenilenir). Alan adı ve tüm üst alan adlarıyla eşleşir. Engellenen istekler `WebResourceRequested` içinde 403 ile kesilir. Kullanıcının açtığı ana sayfa hiçbir zaman engellenmez.
- Açılır pencereler gerçek sekme olarak verilir (`NewWindow`), böylece "Google ile giriş yap" gibi `window.opener` akışları çalışır.
- Araç çubuğu sayfası C#'a komut gönderebildiği için, sayfalardan gelen veriler (başlık vb.) oraya yalnızca `textContent` ile yazılır.
- `Gecit.exe --browser` doğrudan tarayıcıyı açar (masaüstü kısayolu bunu kullanır). Uygulama zaten açıksa ona sinyal gönderilir.

### Oyun Modu

- Algılama (2 saniyede bir): hazır liste (`GameCatalog.Known`, Türkiye'de popüler Metin2/Knight Online/Zula dahil), Steam kütüphaneleri (`libraryfolders.vdf` + `appmanifest_*.acf`), Epic manifestleri, kullanıcının eklediği .exe'ler ve isteğe bağlı olarak `SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN`.
- Oyun başlayınca:
  - Yüksek performans güç planına geçilir (`powercfg`, yönetici gerekmez).
  - Oyuna yüksek öncelik verilir.
  - Seçilen uygulamalar ya **dondurulur** (`NtSuspendProcess` + `EmptyWorkingSet`: işlemci sıfırlanır, RAM sayfa dosyasına gider, kaldığı yerden devam eder) ya da **kapatılır** (`CloseMainWindow`, hiçbir şey zorla öldürülmez; oyun bitince yeniden açılır).
- Oyun işlemi kapanınca hepsi geri alınır. Kullanıcı oyun sırasında güç planını kendisi değiştirdiyse ona dokunulmaz. Çözme işlemi PID ile birlikte başlangıç zamanını da kontrol eder, böylece aynı PID'yi alan başka bir işleme dokunulmaz.
- Çökme güvenliği: oturum, bir şeye dokunulmadan önce `%LOCALAPPDATA%\Gecit\gamemode-session.json` dosyasına yazılır. Geçit çökerse bir sonraki açılışta dondurulanlar çözülür ve güç planı geri alınır (testte doğrulandı).
- Oyunun kendisine bir şey enjekte edilmez, yalnızca öncelik değiştirilir. Hile koruması buna izin vermezse not olarak gösterilir.
- Canlı panel: RAM, CPU (`GetSystemTimes`), GPU kullanımı, VRAM ve sıcaklık (`nvidia-smi`). Oturum raporunda boşalan RAM ve en yüksek GPU sıcaklığı yer alır.

### İnternet

- Canlı bağlantı: internet hedefi (Cloudflare, Google ya da kullanıcının yazdığı oyun sunucusu) ve varsayılan ağ geçidi (modem) saniyede bir ICMP ile pinglenir. Son 120 saniye grafiğe çizilir. Ortalama, dalgalanma (art arda ölçümlerin farkı) ve kayıp hesaplanır. Teşhis şöyle yapılır: modem pingi kötüyse sorun ev ağında (Wi-Fi/kablo), modem iyi ama internet kötüyse sorun operatörde. Döngü yalnızca sayfa açıkken çalışır ve 6 saniye boyunca istek gelmezse kendiliğinden durur.
- Hız testi: `speed.cloudflare.com` üzerinden 6 paralel indirme ve 4 paralel yükleme, her biri 8 saniye. İlk 1,5 saniye TCP yavaş başlangıcı olduğu için ölçüme katılmaz. Test sırasında 1.1.1.1 pinglenir ve gecikme artışına göre bufferbloat notu verilir (A+ <5 ms, A <30, B <60, C <200, D <400, F). Cloudflare 50 MB'tan büyük indirme isteklerine 403 döndürür.
- Uygulama başına trafik: Windows çekirdek ETW oturumunu yalnızca yöneticiye açar. Bu yüzden `Gecit.exe --elevated netmon <pipe> <uiPid>` yardımcı süreci (TraceEvent, `NetworkTCPIP | Process`) saniyede bir PID başına baytları adlandırılmış kanala yazar. Arayüz kanalı kapatınca ya da kendisi kapanınca yardımcı da çıkar (testte doğrulandı). Süreç adları paketin ilk görüldüğü anda alınır, ölmüş süreçler için ProcessStart/DCStart olaylarındaki dosya adı kullanılır.

Bu bağlantıda ölçülenler (2026-10-26, Ethernet): Cloudflare'e 53 ms, modeme ~1 ms, 620 Mbps indirme, 47,8 Mbps yükleme, yük altında +5 ms → not A.

### Ağım

- Yönetici izni gerekmez. `LanScanner` kendi /24 ağındaki 254 adrese iki tur ping atar. Ping'e cevap vermeyen cihazlar da Windows'un ARP sorusuna cevap verdiği için ARP tablosuna (`GetIpNetTable`) düşer. IP ve MAC buradan gelir.
- Adlar şu sırayla aranır, hepsi paralel çalışır:
  - Cihazın kendi arayüzü: Samsung TV `:8001/api/v2/`, Chromecast/Google TV `:8008/setup/eureka_info`, Roku `:8060/query/device-info`. Hepsi yalnızca okuma yapar ve eşleştirme istemez.
  - UPnP/SSDP açıklaması: `friendlyName`, üretici ve model. Modemlerin genel "Internet Home Gateway Device" adı atlanır, modem hep "Modem" diye görünür.
  - Cihazın kendisine mDNS PTR sorgusu (5353), NetBIOS düğüm durumu ve modemin DNS'inde PTR kaydı.
- Üretici, MAC'in ilk yarısından (OUI) bulunur. Veri gömülü `Assets/oui.txt.gz` dosyasından gelir (58 bin kayıt, 540 KB). `tools/build-oui.ps1` bu dosyayı Wireshark'ın IEEE kayıt kopyasından yeniden üretir. Telefonların "gizli MAC" adreslerinde (yerel yönetilen bit) üretici yoktur; bunlar telefon/tablet olarak tahmin edilir.
- Görülen cihazlar ağ başına `%APPDATA%\Gecit\devices.json` dosyasında saklanır. Ağ, modemin MAC adresiyle ayırt edilir, böylece başka bir ağa geçen dizüstü yanlış "yeni cihaz" uyarısı vermez. İlk tarama temel kabul edilir. Sonra ortaya çıkan cihazlar bir gün boyunca "yeni" etiketi taşır ve arka plan denetiminde (10 dakikada bir, oyun sırasında yapılmaz) bildirim gösterilir.
- Bu ağda (2026-10-26): modem Nokia (Taicang T&W üretimi), 7 cihaz. Samsung TV, açıkken kendi API'sinden "Samsung 7 Series (43)" adıyla tanındı. Modem, mDNS çoklu yayınını Wi-Fi ile kablo arasında taşımıyor; telefonların adı bu yüzden çıkmıyor.

### Açılış

- Liste, Görev Yöneticisi'nin Başlangıç sekmesiyle aynıdır: HKCU/HKLM `Run`, `WOW6432Node\Run` ve iki Başlangıç klasörü. Açma/kapama, Görev Yöneticisi'nin kullandığı `Explorer\StartupApproved` değerini yazar: 12 bayt, ilk bayt `02` açık, `03` kapalı (ardından kapatma zamanı). Hiçbir kayıt silinmez.
- Tüm kullanıcılara ait (HKLM) öğeler `Gecit.exe --elevated startup <kaynak> <ad> on|off` ile değiştirilir. Yardımcı süreç yalnızca gerçekten var olan öğelere yazar ve kullanıcı öğelerini reddeder.
- Ölçüm (`BootMeter`): Windows'un kendi açılış günlüğü yalnızca yöneticiye açık olduğu için Geçit kendisi ölçer. Başlangıç noktası explorer.exe'nin başlama zamanıdır (masaüstünün göründüğü an). İşlemci %25'in, disk %30'un altına inip 5 saniye öyle kalınca bilgisayar "kullanıma hazır" sayılır. Bu süre en fazla 3 dakikaya kadar ölçülür.
- Program başına etki, masaüstünden 75 saniye sonra hesaplanır. Öğenin exe'siyle eşleşen ve oturum açıldıktan sonra başlamış süreçlerin, alt süreçleri de dahil olmak üzere, işlemci süresi (`GetProcessTimes`) ve G/Ç baytları (`GetProcessIoCounters`) toplanır. Eşikler Görev Yöneticisi'ninkilerdir: yüksek >1 sn işlemci ya da >3 MB, orta >300 ms ya da >300 KB.
- Ölçüm, Geçit Windows'la açılıyorsa her açılışta kendiliğinden yapılır. Açılmıyorsa "Bir sonraki açılışta ölç" düğmesi `HKCU\...\RunOnce` altına tek seferlik `Gecit.exe --boot-measure` yazar. Bu komut pencere açmadan ölçer, sonucu bildirimle söyler ve kapanır. Son 12 ölçüm `%LOCALAPPDATA%\Gecit\boot-history.json` dosyasında tutulur. BIOS süresi, Windows'un kaydettiği `FwPOSTTime` değeridir.
- Geliştirme sürümünde `GECIT_DESKTOP_AGO=<sn>` ortam değişkeni, masaüstü o kadar saniye önce açılmış gibi ölçüm başlatır, böylece yeniden başlatmadan test edilebilir. Doğrulandı: süreç başına işlemci süreleri `Get-Process` ile aynı çıktı.

### Güncelleme

- `Updater` açılıştan 10 saniye sonra (oturum yeni açıldıysa 90 saniye sonra) ve 12 saatte bir `api.github.com/repos/Furkan-Erg/Gecit/releases/latest` adresine bakar. Ayarlar'dan kapatılabilir.
- Yalnızca adı `Gecit-Setup-*.exe` olan ve GitHub'ın hesapladığı `sha256:` özeti bulunan bir dosya önerilir. Dosya `%LOCALAPPDATA%\Gecit\update` klasörüne iner. Özeti, salt okunur kilitli açık tutulurken kontrol edilir ve kurulum bu kilit altında başlatılır; kontrol ile çalıştırma arasında dosya değiştirilemez.
- Kurulum `/SILENT /RELAUNCH=show|tray` ile **`runas` olmadan** başlatılır. Inno kendini yükseltir ve normal kullanıcıyı hatırlar, böylece yeniden açılan Geçit yönetici olmaz. `runas` ile başlatılsaydı yeni Geçit yönetici olarak açılırdı; testte görüldü ve düzeltildi. Kurulum, eski Geçit'i `--exit` ile kapatır. Hizmetleri durdurur, dosyaları değiştirir, hizmetleri yeniden başlatır ve uygulamayı geri açar.
- Kurulumdaki `taskkill` yalnızca kullanıcı oturumlarına dokunur (`SESSION ne 0`). Oturum 0'daki `Gecit.exe`, GecitDNS hizmetidir ve düzgünce durdurulur.
- Test için geliştirme sürümünde `GECIT_UPDATE_URL` ortam değişkeni sahte bir sürüm adresine yönlendirir. 2026-10-26'da şu testler yapıldı:
  - Kurcalanmış özet reddedildi, kurulum çalışmadı.
  - Gerçek güncelleme 14 saniye sürdü. Hizmetler yeniden başladı ve Geçit yöneticisiz olarak yeniden açıldı.

### Demolar ve Bedava

- Hiçbir API anahtarı ya da hesap gerekmez; Steam ve Epic'in herkese açık mağaza verileri kullanılır (`src/Gecit/Store`).
- Demolar: sıra Steam mağaza aramasından gelir (`category1=10`; yeni = `sort_by=Released_DESC`, yaklaşan = `filter=comingsoon`, oyuncu filtresi = `category3`: 9 co-op, 38 online co-op, 1 çok oyunculu). Ayrıntılar (resim, Türkçe etiketler, Türkçe dil desteği, Steam Deck uyumu, çıkış tarihi) `IStoreBrowseService/GetItems` ile hem demodan hem de ana oyundan alınır.
- Takip edilen demolara 2 saatte bir, duyurulan saati geçmiş bir demo varsa 10 dakikada bir bakılır. Geliştirici tarihi değiştirirse kayıtlı tarih de güncellenir.
- Bedava: Epic'in `freeGamesPromotions` verisinde fiyatı %0'a inen kampanyalar (şimdiki ve gelecek hafta). Steam'de ise indirimdekiler fiyata göre sıralanıp `best_purchase_option.is_free_to_keep` olanlar alınır. Free-to-play oyunlar ve hafta sonu bedava etkinlikleri kalıcı olmadığı için listede yoktur.
- Yeni bedava oyun için bildirim 3 saatte bir, bir kampanya başlayıp biterken ise 10 dakikada bir kontrol edilir. DLC'ler için bildirim gösterilmez.

## Üçüncü taraf dosyalar

`third_party/goodbyedpi/`, [GoodbyeDPI-Turkey](https://github.com/cagritaskn/GoodbyeDPI-Turkey) `release-0.2.3rc3-turkey` sürümünün x86_64 dosyalarıdır. 2026-10-26'da doğrulandı:

| Dosya | SHA-256 | Not |
|---|---|---|
| goodbyedpi.exe | `8d412b09…c430dd1f` | Importları yalnızca WinDivert, CryptGenRandom, servis API'si, CRT, ws2_32. Ağ istemcisi, süreç başlatma ya da kayıt defteri yazma yok. Stringler 0.2.3rc3 kaynağıyla birebir aynı. |
| WinDivert.dll | `6110bfa4…5e6c7e` | Resmî WinDivert 2.2.0-C ile birebir aynı. |
| WinDivert64.sys | `e69b5ba3…e82cf50` | 2.2.0-D sürümü, Cloudveil Technology imzası geçerli. |

WinDivert 2.2.2 Windows Defender tarafından `Misleading:Win32/Malgent!AMTB` olarak işaretleniyor, 2.2.0 işaretlenmiyor. Bu yüzden 2.2.0 kullanılıyor.

Lisanslar `third_party/goodbyedpi/licenses/` içinde: GoodbyeDPI Apache 2.0, WinDivert LGPLv3.

`src/Gecit/Assets/oui.txt.gz`: IEEE MAC adres blokları kaydından (MA-L/MA-M/MA-S) türetilmiştir. Kaynak olarak Wireshark'ın `manuf` kopyası kullanılır, dosya `tools/build-oui.ps1` ile üretilir.

## Lisans

Geçit'in kendi kodu [GPL-3.0](LICENSE) lisanslıdır. Üçüncü taraf dosyalar kendi lisanslarıyla gelir (yukarıda).

## Uyarı

Geçit bir VPN değildir. Kimliğini gizlemez, sadece operatörün DPI ve DNS engellerini aşar. Kullanımdan doğan sorumluluk kullanıcıya aittir.
