# EchoCancel 0.2 — hoparlör açıkken Discord'a yalnızca kendi sesini gönder

> Not: Bu belge 0.2 sürümünün (eski adı YankıKes) ayrıntılı Türkçe teknik notlarıdır. Güncel kullanım için ana [README](../README.md) dosyasına bak.

Windows 11 sistem tepsisinde çalışan küçük bir uygulama. Mikrofonu (ör. Logitech Brio) ve seçtiğin
hoparlörün çıkışını aynı anda dinler, hoparlörden gelen sesi mikrofon sinyalinden çıkarır (akustik
yankı giderme), sustuğunda çıkışı tamamen kapatır ve sonucu Discord'un mikrofon olarak seçebileceği
bir sanal ses kablosuna yazar.

> **Doğrulama durumu.** Ses işleme çekirdeği (AEC, kapı, saat kayması telafisi, gecikme kestirimi)
> Linux'ta gerçek konuşma kayıtları ve sentetik bir odayla çalıştırılıp ölçüldü (aşağıda).
> 0.2 kurulumu bir Windows 11 bilgisayarda (Brio 100, Realtek hoparlör, VB-CABLE, Discord) kurulup açıldı:
> pencere 93 ms'de açıldı, kurulum kontrolünün altı maddesi yeşil çıktı, ayar değişikliği ses grafiğini
> yeniden başlatmadan uygulandı. Dört senaryonun ses kalitesi ölçümü o makinede yapılmadı.

## 1. Nasıl çalışır

```
 Oyun / video / Discord sesi ──► Hoparlör (Windows varsayılan çıkışı; normal çalmaya devam eder)
                                   │
                                   └─ WASAPI loopback (kopya) ─────────────┐ referans
 Brio mikrofonu ── WASAPI yakalama ──► [gecikme] ──► WebRTC AEC3 + gürültü bastırma
                                                          │
                                                     ses kapısı (VAD, ileri bakışlı)
                                                          │
                                   CABLE Input (sanal kablo, uygulama buraya YAZAR)
                                                          │
                                   CABLE Output ◄── Discord "Giriş aygıtı" burayı DİNLER
```

| Parça | Seçim | Neden |
|---|---|---|
| Dil / arayüz | C# (.NET 8), WinForms `NotifyIcon` | Tepsi uygulaması için en az kod; WASAPI'ye olgun erişim |
| Ses G/Ç | NAudio 2.2.1 (WASAPI, paylaşımlı kip) | Loopback, olay güdümlü yakalama, aygıt bildirimleri |
| Yankı giderme | WebRTC Audio Processing Module (AEC3), yerel `webrtc-apm.dll` | Chrome/Meet'in kullandığı olgun, tamamen yerel, yalnızca CPU |
| Gürültü bastırma | Aynı modülün NS'i | Ek maliyet yok denecek kadar az |
| Ses kapısı (VAD) | Kendi hafif kapım (konuşma bandı enerjisi + uyarlanır gürültü tabanı + AEC'nin ne kadar sildiğine bakan koruma) | Paketteki Windows DLL'i WebRTC VAD'i dışa aktarmıyor; Silero gibi sinir ağı VAD'leri ~15 MB bağımlılık ve ek RAM getirir. İlk sürümde hafif olan seçildi |
| Sanal mikrofon | **VB-CABLE** (ayrı kurulur) | Kendi çekirdek sürücümüzü yazmak imza + bakım yükü demek |

**Gecikme ve saat farkı.** Mikrofon, hoparlör ve sanal kablo üç ayrı saatle çalışır. Mikrofon ana
saat kabul edilir; referans ve çıkış akışları, doluluk seviyesini izleyen yavaş bir PI denetleyiciyle
yeniden örneklenen arabelleklerden geçer (`DriftBuffer`). Referansın yankıdan *önce* gelmesi gerekir;
uygulama iki akışın zarflarını ilişkilendirerek gecikmeyi kestirir ve gerekiyorsa mikrofon yoluna
gecikme ekler. İnce hizalamayı AEC3'ün kendi gecikme kestiricisi yapar.

**Güvenlik kuralı.** Ham mikrofon sesi hiçbir koşulda kabloya yazılmaz. Uygulama kapalıyken,
aygıt beklerken, hata olduğunda ya da kapatıldığında Discord sessizlik alır.

## 2. Kurulum (tek tık)

`EchoCancel-Kurulum-0.2.0.exe` dosyasını çalıştır, **Evet** → **Kur** → **Son**. Kurulum programı:

- EchoCancel'i `C:\Program Files\EchoCancel` klasörüne kurar. .NET içinde gelir, ayrıca bir şey kurman gerekmez.
- **VB-CABLE** yoksa üreticinin sitesinden indirip kurar. Windows sürücü onayı sorarsa **Yükle**'ye bas. Bu adım internet ister.
- VB-CABLE, Windows'un varsayılan hoparlörünü ya da mikrofonunu kabloya çevirdiyse eski aygıtlara geri alır.
- Masaüstüne ve Başlat menüsüne **EchoCancel** kısayolu ekler. İstersen Windows açılınca tepside başlatır.
- *Ayarlar → Uygulamalar*'a kaydolur. Kaldırınca VB-CABLE sürücüsü kurulu kalır.

Gereksinim: 64 bit Windows 10 ya da 11. Yerel ses kütüphanesi yalnızca Windows'ta hazır bulunan
`ucrtbase.dll`, `winmm.dll` ve `kernel32.dll` dosyalarına bağlıdır, Visual C++ paketi gerekmez.

**Arkadaşına gönderirken:** Dosya imzasız olduğu için internetten indirildiğinde Windows SmartScreen
"Windows bilgisayarınızı korudu" diyebilir: **Ek bilgi → Yine de çalıştır**. Bazı antivirüsler imzasız
kurulum dosyalarını da sorgulayabilir.

**Pencere:** Saatin yanındaki mikrofon simgesine tıkla. Simgenin rengi durumu gösterir: gri kapalı,
turuncu bekliyor ya da yankıyı öğreniyor, mavi hazır, yeşil sesin gidiyor. Masaüstü kısayoluna tekrar
tıklamak da pencereyi öne getirir. Ayarlar anında uygulanır. Yalnızca aygıt değiştirince ses yaklaşık
1 saniye kesilir; gürültü bastırma, otomatik kazanç ve kelime başı payı değişince ise yankı birkaç
saniye yeniden öğrenilir. **Kurulum kontrolü** düğmesi eksikleri listeler ve tek tıkla giderir.

### Hangi aygıt nerede seçilir

| Yer | Ayar | Seçilecek aygıt |
|---|---|---|
| EchoCancel → Mikrofon | giriş | **Logitech BRIO** mikrofonu (ya da "Windows varsayılanını izle") |
| EchoCancel → Hoparlör (yankı referansı) | dinlenecek çıkış | Sesin gerçekten çıktığı **hoparlör** (ya da varsayılanı izle) |
| EchoCancel → Sanal çıkış | yazılacak kablo | **CABLE Input (VB-Audio Virtual Cable)** — otomatik bulunur |
| Discord → Ses ve Görüntü → Giriş Aygıtı | mikrofon | **CABLE Output (VB-Audio Virtual Cable)** |
| Discord → Ses ve Görüntü → Çıkış Aygıtı | hoparlör | EchoCancel'te seçili olanla **aynı hoparlör** |
| Windows varsayılan çıkış | — | Hoparlörün (CABLE Input **değil**) |

Discord başka bir çıkış aygıtına çalarsa o ses referansta olmaz ve giderilemez.

### Discord ayarları (Ses ve Görüntü)

- **Yankı Engelleme: kapalı.** Discord'un elinde doğru referans yok; işi EchoCancel yapıyor.
- **Gürültü Bastırma:** "Standart" ya da kapalı ile başla. Krisp açık kalabilir ama çift işlem sesi inceltebilir.
- **Otomatik Kazanç Kontrolü:** istersen açık. Sesin kısık geliyorsa EchoCancel'teki "Otomatik kazanç" yerine bunu dene.
- **Giriş Hassasiyeti:** otomatiği kapat, kaydırıcıyı en sola yakın (≈ −90…−80 dB) bırak. Kapıyı EchoCancel
  yönetiyor; ikinci bir kapı kelime başlarını kesebilir.
- Bas-konuş kullanıyorsan aynen devam edebilirsin.

## 3. Dört senaryo testi

Hazırlık: tepsi simgesine tıkla, EchoCancel penceresi açık kalsın; seviye çubukları ve üstteki durum satırı canlı güncellenir. Discord'da
*Ses ve Görüntü → Mikrofon Testi → "Kontrol Edelim"* kendi çıkışını sana geri çalar; ama o geri çalınan
ses de hoparlörden çıkıp referansa girdiği için en temiz test **bir arkadaşla sesli kanalda** ya da
ikinci bir hesapla/telefonla dinleyerek yapılır. Alternatif: Windows *Ses Kaydedici* ya da Audacity'de
kaynak olarak **CABLE Output**'u seçip kaydet.

| # | Senaryo | Ne yap | Beklenen | Pencerede |
|---|---|---|---|---|
| 1 | Yalnızca ben konuşuyorum | Hoparlörde hiçbir şey çalmazken 1'den 20'ye say, aralarda 2 sn sus; "p, t, k, s" ile başlayan kelimeler söyle | Ses doğal, kelime başları tam; susunca tam sessizlik | Konuşurken "AÇIK", susunca ≈0,4 sn sonra "kapalı"; "Discord'a" çubuğu "sessiz" |
| 2 | Yalnızca hoparlör | Sen sus; YouTube'da konuşmalı bir video ve bir oyun sesi çal (normal ses seviyende), 1 dk | Karşı taraf hiçbir şey duymamalı (ilk saniyelerde kapı zaten kapalıdır) | "Hoparlör etkin: evet", kapı "kapalı", "AEC+NS'in sildiği" 20 dB üstü |
| 3 | İkisi aynı anda | Video çalarken normal sesle konuş | Sesin anlaşılır gider; video sesi arkada ya hiç yok ya çok kısık. Kelime başlarında hafif yumuşama olabilir (AEC3'ün çift konuşma davranışı) | Kapı "AÇIK"; sen susunca ≈0,4 sn içinde "kapalı" |
| 4 | Aygıt değişimi | (a) Brio'nun USB'sini çek-tak. (b) Windows varsayılan çıkışını kulaklığa, sonra geri hoparlöre al. (c) Çalışırken VB-CABLE'ı Aygıt Yöneticisi'nden devre dışı bırak-etkinleştir | Her birinde simge turuncuya döner, Discord sessizlik alır, aygıt dönünce ≈1–3 sn içinde yeşile döner. Ham mikrofon hiç gitmemeli | Durum satırı "… bekleniyor" → "Çalışıyor"; `log.txt`'de geçişler |

Sorun çıkarsa: menü → *Günlük ve ayar klasörünü aç* → `log.txt`.

**Ayar ipuçları**

- Hoparlör sesi sızıyorsa: *Ayarlar → Hoparlör çalarken koruma → Sıkı*; hoparlörü kıs ya da kameradan uzaklaştır.
- Video çalarken sesin kesiliyorsa: *Hafif*; ya da *Kapı hassasiyeti → Yüksek*.
- Kelime başları kesiliyorsa: *Kelime başı payı → 50 ms*.
- "boşalma" sayaçları sürekli artıyorsa: `settings.json` içinde `ReferenceBufferMs` / `OutputBufferMs` değerini 30 → 50 yap.

## 4. CPU, RAM ve gecikme nasıl ölçülür

Aşağıdakilerin hiçbiri senin makinende ölçülmedi; kendi değerlerini böyle alırsın.

**CPU / RAM**

- Pencerenin altındaki satır: işlem CPU yüzdesi (toplam CPU'ya oranla), özel RAM ve tahmini gecikme. Pencere
  açıkken arayüzün kendi yükü de sayıya dahildir; pencere gizliyken yenileme durur.
- Pencerenin açılma süresi `log.txt`'ye "Pencere açıldı: … ms" olarak yazılır.
- PowerShell ile 60 saniyelik ortalama (oyun açıkken ve kapalıyken ayrı ayrı):
  ```powershell
  $n=[Environment]::ProcessorCount
  (Get-Counter '\Process(EchoCancel)\% Processor Time' -SampleInterval 1 -MaxSamples 60).CounterSamples |
    Measure-Object CookedValue -Average -Maximum | % { "ort %{0:N2}  en çok %{1:N2} (toplam CPU'ya oranla)" -f ($_.Average/$n),($_.Maximum/$n) }
  Get-Process EchoCancel | % { "RAM: çalışma kümesi {0:N0} MB, özel {1:N0} MB" -f ($_.WorkingSet64/1MB),($_.PrivateMemorySize64/1MB) }
  ```
- Yalnızca ses işleme maliyeti (aygıtsız, senin işlemcinde): `bin\tools\EchoCancel.Sim.exe bench 60`

**Eklenen gecikme**

- Pencerenin altındaki "Gecikme ≈" değeri bir *tahmindir* (ayarlanan gecikmeler + ölçülen arabellek doluluğu).
- Gerçek uçtan uca ölçüm: OBS'te iki ses kaynağı ekle — *Brio mikrofonu* (İz 1) ve *CABLE Output* (İz 2) —
  Gelişmiş Ses Özellikleri'nde izleri ayır, çok izli (mkv) kayıt al, hoparlör kapalıyken birkaç kez el çırp.
  Kaydı Audacity'de aç; iki izdeki çırpma tepeleri arasındaki fark EchoCancel'in eklediği gecikmedir.
  (Discord'un kendi kodlama/ağ gecikmesi bunun dışındadır.)
- Düşürmek için: *Kelime başı payı* 30 → 10/20 ms; `OutputBufferMs` 30 → 20.

## 5. Çevrimdışı ölçümler (Linux, sentetik oda — senin odan değil)

`EchoCancel.Sim sim` gerçek işlem zincirini iki açık konuşma kaydıyla (yakın uç: 11 sn'lik tek konuşmacı;
uzak uç: 60 sn'lik konuşma) ve yapay bir odayla (45 ms doğrudan yol, ≈0,3 sn yankılanım, hoparlör saati
+150 ppm kaymış, paketler düzensiz) çalıştırır. Doğrusal ve gürültüsüz bir oda olduğu için gerçek odadan
**daha iyimser** sonuç verir. Varsayılan ayarlarla alınan sonuçlar:

| Ölçüm | Sonuç |
|---|---|
| Yalnız hoparlör, yakınsamış: mikrofondaki yankı → AEC+NS sonrası | −31,9 → −80,4 dBFS (48,4 dB). Diğer çeşitlerde 41–61 dB |
| Yalnız hoparlör: kapının yanlışlıkla açık kaldığı süre | 0 sn / 33 sn (soğuk başlangıç ve yeniden başlatma dahil; denenen 5 çeşidin hepsinde 0) |
| Soğuk başlangıçta bastırmanın kalıcı olarak 20 dB'yi geçmesi | ≈1,6 sn (bu sürede kapı kapalı tutulduğu için çıkışa sızıntı: 0) |
| Yalnız ben: konuşma çerçevelerinin kapıdan geçme oranı | %100; cümle başlarında kapı kaynaklı kayıp 0,00 dB; AEC+NS'in seviyeye etkisi −0,2 dB |
| İkisi birden, yankı sesimden 5 dB kısık | Konuşma çerçevelerinin %97,6'sı geçti (saat kayması 0 ppm çeşidinde %93,2). Cümle başının ilk 120 ms'inde AEC kaynaklı seviye düşüşü ort. 2,7 dB, en kötü 5,7 dB (0 ppm çeşidinde ort. 5,7 / en kötü 16,4 dB) |
| İkisi birden, yankı sesimden 17 dB kısık | %99,4 geçti; cümle başı düşüşü ort. 1,5 dB, en kötü 2,6 dB |
| İkisi birden, yankı sesimle aynı yükseklikte | %82,0 geçti; cümle başı düşüşü ort. 6,8 dB, en kötü 16,1 dB — bu durumda sesin belirgin biçimde kesilir |
| Yankı referanstan önce gelirse (8 ms'lik yol) | 4. saniyede mikrofon gecikmesi 10 → 35 ms'ye otomatik çıktı; sonrasında bastırma 54,7 dB; yanlış açılma 0 |
| İşlem zincirinin sabit gecikmesi | 55 ms (10 mikrofon gecikmesi + ≈15 APM + 30 ileri bakış); arabellekler ve WASAPI hariç |
| 10 ms'lik çerçeve başına işlem süresi (2,1 GHz Xeon sanal makine, tek çekirdek) | ort. ≈92–100 µs (tek çekirdeğin ≈%1'i), %99'luk dilim ≈145–185 µs, en uzun ≈2,1 ms |
| Saat kayması telafisi (öz sınama, 200 ppm, 10 dk) | Öğrenilen düzeltme 200 ppm, boşalma/taşma 0. Döngü bilerek yavaştır: ilk çalıştırmada oturması birkaç dakika sürer, sonra değer saklanır |

Ham çıktı: `docs/simulasyon-sonuclari.txt`. Yeniden üretmek için: `EchoCancel.Sim selftest` ve
`EchoCancel.Sim sim yakin.wav uzak.wav [--early] [--echo-db N] [--ppm N]`.

Kendi kayıtlarınla denemek için: `EchoCancel.Sim file mikrofon.wav hoparlor.wav cikis.wav`

## 6. Bilinen sınırlar

- **Çift konuşma:** Hoparlör sesi mikrofonda senin sesin kadar ya da daha yüksekse AEC3 kelime başlarını
  yumuşatır, kapı da sesini kesebilir. Hoparlörü kısmak / kameradan uzaklaştırmak en etkili çaredir.
- **Başlangıç:** Açılıştan ve her aygıt değişiminden sonra, hoparlörün çaldığı ilk ≈2,5 saniyede kapı
  kapalı tutulur (AEC odayı öğrenirken sızıntı olmasın diye). Bir aygıt üçlüsüyle *ilk* çalıştırmada bu süre,
  referans–mikrofon gecikmesi ölçülene kadar uzar (hoparlör çalarken ≈4 sn, en çok 8 sn). O anda konuşursan
  başı kesilir. Menüden kapatılabilir ("Başlangıçta yankı öğrenilene kadar sustur").
- **Yalnızca seçili hoparlör referanstır.** Ses başka bir aygıttan çıkarsa giderilemez. "Varsayılanı izle"
  seçiliyken varsayılan değişince uygulama ≈1 sn içinde yeni aygıta geçer.
- **VAD basittir:** klavye tıkırtısı, alkış gibi konuşma bandındaki yüksek sesler kapıyı açabilir.
- **Uygulama kapalıyken** Discord sessizlik alır; EchoCancel'siz konuşmak için Discord girişini Brio'ya geri al.
- Hoparlörün ses seviyesini değiştirmek ya da mikrofonu oynatmak AEC'nin ≈1 sn yeniden uyum sağlamasını gerektirir.

## 7. Kaynaktan derleme

Gerekenler: .NET 10 SDK; kurulum dosyası için NSIS 3 (`makensis`, Windows'ta ya da Linux'ta çalışır).

```powershell
cd EchoCancel
dotnet publish src\EchoCancel.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -o dist\pub
makensis -INPUTCHARSET UTF8 -DPAYLOAD=dist\pub -DVERSION=0.2.0 -DOUTFILE=dist\EchoCancel-Kurulum-0.2.0.exe installer\EchoCancel.nsi
# Ölçüm aracı
dotnet publish src\EchoCancel.Sim -c Release -r win-x64 --self-contained false -o dist\tools
```

Komut satırı modları (kurulum programı kullanır): `EchoCancel.exe --ensure-cable` (VB-CABLE yoksa indir/kur,
varsayılanları geri al), `--fix-defaults`, `--save-defaults`, `--tray` (pencereyi açmadan başla).

| Klasör | İçerik |
|---|---|
| `src/EchoCancel.Core` | Platformdan bağımsız çekirdek: APM sarmalayıcı, `EchoPipeline`, `VoiceGate`, `DriftBuffer`, `DelayEstimator` |
| `src/EchoCancel.App` | Windows uygulaması: `AudioEngine` (WASAPI, aygıt önbelleği, toparlanma), `MainForm` ve `Ui` (arayüz), `SetupForm` ve `Prereq` (kurulum kontrolü, VB-CABLE, varsayılan aygıtlar) |
| `src/EchoCancel.Sim` | Çevrimdışı sınama: `selftest`, `bench`, `file`, `sim` |
| `installer/EchoCancel.nsi` | Tek tıklık kurulum betiği |

Ayarlar ve günlük: `%AppData%\EchoCancel\settings.json`, `log.txt`.

## 8. Üçüncü taraf bileşenler

- WebRTC Audio Processing — BSD 3-Clause (The WebRTC Project Authors)
- SoundFlow.Extensions.WebRtc.Apm (yerel ikili paketi) — MIT (LSXPrime)
- NAudio — MIT (Mark Heath)
- NSIS (kurulum programı) — zlib/libpng lisansı
- VB-CABLE — VB-Audio Software; ayrı indirilir, bu pakete dahil değildir
