using System.Globalization;

namespace ReplayRecorder.Core;

/// <summary>
/// UI language. English text is the key; <see cref="T(string)"/> returns the Turkish text when
/// Turkish is on and a translation exists, otherwise the English. Set once at startup from settings.
/// </summary>
public static class Lang
{
    public static bool Turkish { get; private set; }

    /// <summary>Culture for dates and numbers shown in the UI.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-GB");

    /// <summary>Switches language; true if it changed.</summary>
    public static bool Use(string language)
    {
        bool tr = language == "tr", changed = tr != Turkish;
        Turkish = tr;
        return changed;
    }

    /// <summary>Line in release notes that starts the Turkish part (release.ps1 -NotesTr adds it).</summary>
    public const string NotesTrMarker = "<!-- tr -->";

    /// <summary>The part of the release notes in the current language; English when there is no Turkish part.</summary>
    public static string PickNotes(string notes)
    {
        int i = notes.IndexOf(NotesTrMarker, StringComparison.Ordinal);
        if (i < 0) return notes;
        var tr = notes[(i + NotesTrMarker.Length)..].Trim();
        return Turkish && tr.Length > 0 ? tr : notes[..i].Trim();
    }

    /// <summary>Text that may already be in either language, put into the current one (for live switching).</summary>
    public static string Retranslate(string s) => (Reverse.Value.TryGetValue(s, out var en) ? en : s).T();

    // Turkish -> English for plain texts (format strings never appear on screen unfilled).
    static readonly Lazy<Dictionary<string, string>> Reverse = new(() =>
        Tr.Where(kv => !kv.Key.Contains('{')).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key));

    public static string T(this string en) => Turkish && Tr.TryGetValue(en, out var t) ? t : en;

    /// <summary>Translates a format string like "Exporting {0} of {1}…", then fills it in.</summary>
    public static string T(this string en, params object?[] args) => string.Format(Culture, en.T(), args);

    internal static IReadOnlyDictionary<string, string> Entries => Tr;

    // shortcut: one dictionary in code, fine for two languages; move to resource files if a third comes.
    static readonly Dictionary<string, string> Tr = new()
    {
        // ---------------------------------------------------------------- library window
        ["Replay Recorder"] = "Replay Recorder",
        ["Library"] = "Kitaplık",
        ["All clips"] = "Tüm klipler",
        ["Favorites"] = "Favoriler",
        ["Today"] = "Bugün",
        ["Yesterday"] = "Dün",
        ["Today {0:HH:mm}"] = "Bugün {0:HH:mm}",
        ["Yesterday {0:HH:mm}"] = "Dün {0:HH:mm}",
        ["1 clip"] = "1 klip",
        ["{0} clips"] = "{0} klip",
        ["{0} clips · {1}\n{2}"] = "{0} klip · {1}\n{2}",
        ["Search clips"] = "Kliplerde ara",
        ["Show clips from one game"] = "Tek bir oyunun kliplerini göster",
        ["Newest"] = "En yeni",
        ["Oldest"] = "En eski",
        ["Longest"] = "En uzun",
        ["Largest"] = "En büyük",
        ["Open folder"] = "Klasörü aç",
        ["Settings"] = "Ayarlar",
        ["Settings (version {0})"] = "Ayarlar (sürüm {0})",
        ["Version {0}"] = "Sürüm {0}",
        ["No clips yet"] = "Henüz klip yok",
        ["Press your save-clip hotkey in a game and it shows up here."] = "Bir oyunda klip kaydetme kısayoluna bas, klip burada görünür.",
        ["Press {0} in a game and the clip shows up here."] = "Bir oyunda {0} tuşuna bas, klip burada görünür.",
        ["No clips match your filter."] = "Filtreye uyan klip yok.",
        ["Nothing here"] = "Burada bir şey yok",
        ["Favorite"] = "Favori",
        ["Select"] = "Seç",
        ["Select every clip from this day"] = "Bu günün tüm kliplerini seç",
        ["Open"] = "Aç",
        ["Rename…"] = "Yeniden adlandır…",
        ["Show in folder"] = "Klasörde göster",
        ["Copy files"] = "Dosyaları kopyala",
        ["Copy file"] = "Dosyayı kopyala",
        ["Select whole day"] = "Tüm günü seç",
        ["Delete"] = "Sil",
        ["Delete {0} clips"] = "{0} klibi sil",
        ["{0}  ·  {1} total"] = "{0}  ·  toplam {1}",
        ["Del"] = "Del",
        ["F2"] = "F2",
        ["Add to favorites"] = "Favorilere ekle",
        ["Remove from favorites"] = "Favorilerden çıkar",
        ["Export for Discord"] = "Discord için dışa aktar",
        ["Export {0} clips for Discord"] = "{0} klibi Discord için dışa aktar",
        ["1 clip selected"] = "1 klip seçildi",
        ["{0} clips selected"] = "{0} klip seçildi",
        ["Clear selection (Esc)"] = "Seçimi temizle (Esc)",
        ["Copy the original clips, then paste them anywhere (Ctrl+V)"] = "Orijinal klipleri kopyala, sonra istediğin yere yapıştır (Ctrl+V)",
        ["Export each selected clip under the Discord size limit, with its saved trim and volumes"] = "Seçili her klibi kayıtlı kırpma ve ses ayarlarıyla Discord boyut sınırının altında dışa aktar",
        ["Delete for good (Del)"] = "Kalıcı olarak sil (Del)",
        ["Copied 1 file: paste with Ctrl+V"] = "1 dosya kopyalandı: Ctrl+V ile yapıştır",
        ["Copied {0} files: paste with Ctrl+V"] = "{0} dosya kopyalandı: Ctrl+V ile yapıştır",
        ["Rename clip"] = "Klibi yeniden adlandır",
        ["There is already a clip called \"{0}\" in this folder."] = "Bu klasörde zaten \"{0}\" adında bir klip var.",
        ["Couldn't rename the clip: {0}"] = "Klip yeniden adlandırılamadı: {0}",
        ["{0} clips ({1})"] = "{0} klip ({1})",
        ["\n\nThis clip is a favorite."] = "\n\nBu klip bir favori.",
        ["\n\n{0} of them are favorites."] = "\n\nBunlardan {0} tanesi favori.",
        ["Delete {0}?{1}\n\nIt's deleted for good, not moved to the Recycle Bin."] = "{0} silinsin mi?{1}\n\nKalıcı olarak silinir, Geri Dönüşüm Kutusu'na gitmez.",
        ["Delete {0}?{1}\n\nThey're deleted for good, not moved to the Recycle Bin."] = "{0} silinsin mi?{1}\n\nKalıcı olarak silinirler, Geri Dönüşüm Kutusu'na gitmezler.",
        ["Delete clip"] = "Klibi sil",
        ["Delete clips"] = "Klipleri sil",
        ["Couldn't delete 1 clip (open in another app?)."] = "1 klip silinemedi (başka bir uygulamada açık mı?).",
        ["Couldn't delete {0} clips (open in another app?)."] = "{0} klip silinemedi (başka bir uygulamada açık mı?).",

        // update banner
        ["Update available"] = "Güncelleme var",
        ["Update now"] = "Şimdi güncelle",
        ["Updated ✓"] = "Güncellendi ✓",
        ["You're now on version {0} (was {1})."] = "Artık {0} sürümündesin (önceki: {1}).",
        ["Version {0} is ready (you have {1}). Takes a few seconds; your clips and settings stay."] = "{0} sürümü hazır (sendeki: {1}). Birkaç saniye sürer; klipler ve ayarlar korunur.",
        ["Wait for the export to finish, then update."] = "Dışa aktarma bitince güncelle.",
        ["Downloading… {0:P0}"] = "İndiriliyor… {0:P0}",
        ["Installing…"] = "Kuruluyor…",
        ["Replay Recorder will close and reopen by itself in a few seconds."] = "Replay Recorder birkaç saniye içinde kapanıp kendiliğinden yeniden açılacak.",
        ["Update failed: {0}"] = "Güncelleme başarısız: {0}",
        ["Try again"] = "Tekrar dene",
        ["Got it"] = "Tamam",
        ["What's new"] = "Yenilikler",
        ["What's new in {0}"] = "{0} sürümündeki yenilikler",

        // rename dialog
        ["Rename"] = "Yeniden adlandır",
        ["Cancel"] = "İptal",
        ["New name"] = "Yeni ad",
        ["Enter a name."] = "Bir ad gir.",
        ["A name can't contain \\ / : * ? \" < > |"] = "Ad şu karakterleri içeremez: \\ / : * ? \" < > |",
        ["A name can't end with a dot."] = "Ad nokta ile bitemez.",

        // ---------------------------------------------------------------- player
        ["Back to library (Esc)"] = "Kitaplığa dön (Esc)",
        ["Play / pause (Space)"] = "Oynat / duraklat (Boşluk)",
        ["Previous frame (←)"] = "Önceki kare (←)",
        ["Next frame (→)"] = "Sonraki kare (→)",
        ["Set start  (I)"] = "Başlangıç  (I)",
        ["Set end  (O)"] = "Bitiş  (O)",
        ["Preview trim  (P)"] = "Kırpmayı önizle  (P)",
        ["Reset"] = "Sıfırla",
        ["Trimmed: {0} of {1}"] = "Kırpıldı: {0} / {1}",
        ["Game volume"] = "Oyun sesi",
        ["Game + chat"] = "Oyun + sohbet",
        ["Game + voice chat"] = "Oyun + sesli sohbet",
        ["Microphone"] = "Mikrofon",
        ["Your mic"] = "Mikrofonun",
        ["This clip has a single audio track"] = "Bu klipte tek ses parçası var",
        ["The player couldn't start: {0}"] = "Oynatıcı başlatılamadı: {0}",
        [Ffmpeg.MpvInstallHint] = "mpv oynatıcı bulunamadı. Şununla kur:  winget install shinchiro.mpv  (sonra Replay Recorder'ı yeniden aç)",

        // ---------------------------------------------------------------- export
        ["Export the trimmed range as an MP4 under the Discord size limit (E)"] = "Kırpılan kısmı Discord boyut sınırının altında MP4 olarak dışa aktar (E)",
        ["Drag into Discord"] = "Discord'a sürükle",
        ["Drag this into a Discord chat"] = "Bunu bir Discord sohbetine sürükle",
        ["Drag into a Discord chat (Discord takes up to 10 files per message)"] = "Bir Discord sohbetine sürükle (Discord mesaj başına en fazla 10 dosya alır)",
        ["Then press Ctrl+V in Discord"] = "Sonra Discord'da Ctrl+V'ye bas",
        ["Preparing…"] = "Hazırlanıyor…",
        ["Starting export…"] = "Dışa aktarma başlıyor…",
        ["{0} clip · target {1:0.#} MB · original is never changed"] = "{0} klip · hedef {1:0.#} MB · orijinal hiç değişmez",
        ["Ready for Discord"] = "Discord için hazır",
        [" · {0} passes to fit"] = " · sığdırmak için {0} geçiş",
        [" · starts at the nearest keyframe"] = " · en yakın ana kareden başlar",
        ["Export cancelled"] = "Dışa aktarma iptal edildi",
        ["Copied! Press Ctrl+V in Discord"] = "Kopyalandı! Discord'da Ctrl+V'ye bas",
        ["This export was cleaned up after {0:0} minutes. Export it again."] = "Bu dışa aktarma {0:0} dakika sonra temizlendi. Tekrar dışa aktar.",
        ["These exports were cleaned up after {0:0} minutes. Export them again."] = "Bu dışa aktarmalar {0:0} dakika sonra temizlendi. Tekrar dışa aktar.",
        ["{0} of {1} exports were cleaned up; using the other {2}."] = "{1} dışa aktarmadan {0} tanesi temizlendi; kalan {2} tanesi kullanılıyor.",
        ["Exporting {0} of {1}…"] = "Dışa aktarılıyor: {0}/{1}…",
        ["{0} of {1}: {2}"] = "{0}/{1}: {2}",
        ["All exported"] = "Hepsi dışa aktarıldı",
        ["{0} failed (see log)"] = "{0} başarısız (günlüğe bak)",
        ["Drag {0} file into Discord  ·  {1:0.0} MB"] = "{0} dosyayı Discord'a sürükle  ·  {1:0.0} MB",
        ["Drag {0} files into Discord  ·  {1:0.0} MB"] = "{0} dosyayı Discord'a sürükle  ·  {1:0.0} MB",
        ["{0}p (original quality, no re-encode)"] = "{0}p (orijinal kalite, yeniden kodlama yok)",
        ["Cutting without re-encoding…"] = "Yeniden kodlamadan kesiliyor…",
        ["Encoding {0}"] = "Kodlanıyor: {0}",
        ["Analysing ({0})"] = "İnceleniyor ({0})",
        ["Done"] = "Bitti",

        // ---------------------------------------------------------------- settings
        ["Replay Recorder Settings"] = "Replay Recorder Ayarları",
        ["Save"] = "Kaydet",
        ["Browse…"] = "Gözat…",
        ["General"] = "Genel",
        ["Instant replay"] = "Anında tekrar",
        ["Video"] = "Video",
        ["Audio"] = "Ses",
        ["Folders and storage"] = "Klasörler ve depolama",
        ["Discord export"] = "Discord dışa aktarma",
        ["Look"] = "Görünüm",
        ["Changes right away; Save keeps it"] = "Hemen değişir; Kaydet ile kalıcı olur",
        ["Accent colour"] = "Vurgu rengi",
        ["Buttons, selection and the trim bar"] = "Düğmeler, seçim ve kırpma çubuğu",
        ["Auto: the look's own colour"] = "Otomatik: görünümün kendi rengi",
        ["Pick any colour"] = "İstediğin rengi seç",
        ["Custom…"] = "Özel…",
        ["Amber"] = "Kehribar", ["Blue"] = "Mavi", ["Cyan"] = "Camgöbeği", ["Green"] = "Yeşil", ["Orange"] = "Turuncu",
        ["Pink"] = "Pembe", ["Red"] = "Kırmızı", ["Violet"] = "Mor",
        ["Windows (blue)"] = "Windows (mavi)",
        ["Language"] = "Dil",
        ["Applies when you save"] = "Kaydedince uygulanır",
        ["Start recorder with Windows"] = "Kaydediciyi Windows ile başlat",
        ["\"Clip saved\" pop-ups"] = "\"Klip kaydedildi\" bildirimleri",
        ["Pop-ups can't show over exclusive-fullscreen games"] = "Bildirimler özel tam ekran oyunların üstünde görünemez",
        ["Sound when a clip is saved"] = "Klip kaydedilince ses çal",
        ["Replay length"] = "Tekrar uzunluğu",
        ["Save clip hotkey"] = "Klip kaydetme kısayolu",
        ["Start/stop full recording"] = "Tam kaydı başlat/durdur",
        ["Avoid Alt+F10 / Alt+F9 / Alt+Z: the NVIDIA overlay uses them. Either Alt key works for Alt hotkeys."] = "Alt+F10 / Alt+F9 / Alt+Z kullanma: NVIDIA arayüzü bunları kullanıyor. Alt kısayolları için iki Alt tuşu da çalışır.",
        ["Keep the replay buffer in RAM"] = "Tekrar arabelleğini RAM'de tut",
        ["Record"] = "Kayıt",
        ["Monitor to record"] = "Kaydedilecek monitör",
        ["Resolution"] = "Çözünürlük",
        ["Frame rate"] = "Kare hızı",
        ["Quality"] = "Kalite",
        ["Encoder"] = "Kodlayıcı",
        ["Codec"] = "Codec",
        ["Capture method"] = "Yakalama yöntemi",
        ["Record mouse cursor"] = "Fare imlecini kaydet",
        ["Saving restarts the recorder's capture (about a second)."] = "Kaydetmek, kaydedicinin yakalamasını yeniden başlatır (yaklaşık bir saniye).",
        ["Output device"] = "Çıkış cihazı",
        ["Everything you hear, Discord included"] = "Duyduğun her şey, Discord dahil",
        ["Mic device"] = "Mikrofon cihazı",
        ["Its own track, so you can rebalance it later"] = "Kendi parçası var, sonradan ayarlayabilirsin",
        ["Mic boost"] = "Mikrofon güçlendirme",
        ["Mic leveling"] = "Mikrofon seviyeleme",
        ["Evens out quiet talk and shouting"] = "Sessiz konuşmayla bağırmayı dengeler",
        ["Level"] = "Seviye",
        ["Level (with boost)"] = "Seviye (güçlendirmeyle)",
        ["Talk normally: aim for the bar to reach the middle"] = "Normal konuş: çubuğun ortaya gelmesini hedefle",
        ["Clips folder"] = "Klip klasörü",
        ["Discord exports folder"] = "Discord dışa aktarma klasörü",
        ["Replay buffer folder"] = "Tekrar arabelleği klasörü",
        ["Rolling temp files (~300 MB). An SSD is best."] = "Dönen geçici dosyalar (~300 MB). En iyisi SSD.",
        ["Warn when clips exceed"] = "Klipler şunu aşınca uyar",
        ["GB · 0 = never warn"] = "GB · 0 = asla uyarma",
        ["Optional"] = "İsteğe bağlı",
        ["Game names"] = "Oyun adları",
        ["One per line: game.exe = Folder name. Unknown games use the window's product name."] = "Her satıra bir tane: oyun.exe = Klasör adı. Bilinmeyen oyunlar pencerenin ürün adını kullanır.",
        ["Maximum file size"] = "En büyük dosya boyutu",
        ["MB · Discord free limit is 20"] = "MB · Discord'un ücretsiz sınırı 20",
        ["Nitro: up to 500. Exports are never larger than this."] = "Nitro: 500'e kadar. Dışa aktarmalar hiçbir zaman bundan büyük olmaz.",
        ["Using {0:0.0} GB right now"] = "Şu an {0:0.0} GB kullanılıyor",
        ["{0}:{1:00} min"] = "{0}:{1:00} dk",
        ["{0} s"] = "{0} sn",
        ["No constant SSD writes. Uses about {0}–{1} MB of RAM for {2}."] = "SSD'ye sürekli yazma yok. {2} için yaklaşık {0}–{1} MB RAM kullanır.",
        ["Automatic: primary monitor"] = "Otomatik: ana monitör",
        ["Automatic: primary monitor ({0})"] = "Otomatik: ana monitör ({0})",
        ["Native (up to 1440p) · recommended"] = "Yerel (1440p'ye kadar) · önerilen",
        ["High (bigger files)"] = "Yüksek (daha büyük dosyalar)",
        ["Balanced · recommended"] = "Dengeli · önerilen",
        ["Smaller files"] = "Daha küçük dosyalar",
        ["Automatic (NVENC on the NVIDIA GPU)"] = "Otomatik (NVIDIA GPU'da NVENC)",
        ["CPU x264 (heavy, last resort)"] = "CPU x264 (ağır, son çare)",
        ["H.264 · plays everywhere · recommended"] = "H.264 · her yerde oynar · önerilen",
        ["HEVC (smaller, less compatible)"] = "HEVC (daha küçük, daha az uyumlu)",
        ["Windows Graphics Capture · recommended"] = "Windows Graphics Capture · önerilen",
        ["Desktop Duplication (native resolution only)"] = "Desktop Duplication (yalnızca yerel çözünürlük)",
        ["Off"] = "Kapalı",
        ["Light"] = "Hafif",
        ["Medium"] = "Orta",
        ["Strong"] = "Güçlü",
        ["Loading devices…"] = "Cihazlar yükleniyor…",
        ["Automatic (real device behind the Windows default)"] = "Otomatik (Windows varsayılanının arkasındaki gerçek cihaz)",
        ["{0}  (virtual: may record silence)"] = "{0}  (sanal: sessizlik kaydedebilir)",
        ["Windows default (communications) mic"] = "Windows varsayılan (iletişim) mikrofonu",
        ["Press keys…  (Esc cancel, Backspace clear)"] = "Tuşlara bas…  (Esc iptal, Backspace temizle)",
        ["Press keys…  (Esc cancel)"] = "Tuşlara bas…  (Esc iptal)",
        ["Not set"] = "Ayarlanmadı",
        ["This screen is driven by {0}, not the RTX GPU (hybrid/Optimus mode). Replay Recorder will use Intel Quick Sync; for best results switch the MUX to discrete-GPU mode."] =
            "Bu ekranı RTX GPU değil {0} sürüyor (hibrit/Optimus modu). Replay Recorder Intel Quick Sync kullanacak; en iyi sonuç için MUX'u harici GPU moduna al.",
        ["HDR is on for this screen. Clips are recorded in SDR, so very bright highlights may clip."] = "Bu ekranda HDR açık. Klipler SDR kaydedilir, bu yüzden çok parlak yerler patlayabilir.",
        ["Storage warning must be a number of GB (0 = off)."] = "Depolama uyarısı GB cinsinden bir sayı olmalı (0 = kapalı).",
        ["Maximum file size must be between 1 and 500 MB."] = "En büyük dosya boyutu 1 ile 500 MB arasında olmalı.",
        ["Set a save-clip hotkey."] = "Bir klip kaydetme kısayolu ayarla.",
        ["The two hotkeys must be different."] = "İki kısayol farklı olmalı.",
        ["Clips folder can't be created: {0}"] = "Klip klasörü oluşturulamıyor: {0}",
        ["Discord exports folder can't be created: {0}"] = "Discord dışa aktarma klasörü oluşturulamıyor: {0}",
        ["Replay buffer folder can't be created: {0}"] = "Tekrar arabelleği klasörü oluşturulamıyor: {0}",
        ["Put the Discord exports folder outside the clips folder, so exports don't show up as clips."] = "Discord dışa aktarma klasörünü klip klasörünün dışına koy, yoksa dışa aktarmalar klip gibi görünür.",
        ["Couldn't save settings: {0}"] = "Ayarlar kaydedilemedi: {0}",

        // ---------------------------------------------------------------- tray and recorder
        ["Starting…"] = "Başlıyor…",
        ["Save clip"] = "Klip kaydet",
        ["Start recording"] = "Kaydı başlat",
        ["Stop recording"] = "Kaydı durdur",
        ["Pause"] = "Duraklat",
        ["Resume"] = "Devam et",
        ["Start with Windows"] = "Windows ile başlat",
        ["Install update"] = "Güncellemeyi yükle",
        ["Install update {0}"] = "{0} güncellemesini yükle",
        ["Open library"] = "Kitaplığı aç",
        ["Open clips folder"] = "Klip klasörünü aç",
        ["Settings…"] = "Ayarlar…",
        ["Open log"] = "Günlüğü aç",
        ["Quit Replay Recorder"] = "Replay Recorder'dan çık",
        ["Replay Recorder updated"] = "Replay Recorder güncellendi",
        ["Now on version {0} (was {1})."] = "Artık {0} sürümü (önceki: {1}).",
        ["Replay Recorder update"] = "Replay Recorder güncellemesi",
        ["Version {0} is ready. Right-click the tray icon > Install update."] = "{0} sürümü hazır. Tepsi simgesine sağ tıkla > Güncellemeyi yükle.",
        ["Stop the full recording first, then install the update."] = "Önce tam kaydı durdur, sonra güncellemeyi yükle.",
        ["Downloading update…"] = "Güncelleme indiriliyor…",
        ["Installing the update. The recorder restarts by itself in a few seconds."] = "Güncelleme kuruluyor. Kaydedici birkaç saniye içinde kendiliğinden yeniden başlar.",
        ["Update failed"] = "Güncelleme başarısız",
        ["Couldn't register the save-clip hotkey {0}. Change it in settings."] = "{0} klip kaydetme kısayolu kaydedilemedi. Ayarlardan değiştir.",
        ["Paused"] = "Duraklatıldı",
        ["Stopped"] = "Durduruldu",
        ["Error"] = "Hata",
        ["● Full recording in progress"] = "● Tam kayıt sürüyor",
        ["Recording {0} · buffer {1}s"] = "{0} kaydediliyor · arabellek {1} sn",
        ["Recording {0} · buffer {1} of {2}s ready"] = "{0} kaydediliyor · arabellek {1}/{2} sn hazır",
        ["Clip saved"] = "Klip kaydedildi",
        ["{0} · last {1}s"] = "{0} · son {1} sn",
        ["Clip not saved"] = "Klip kaydedilmedi",
        ["See the log for details"] = "Ayrıntılar için günlüğe bak",
        ["Recording started"] = "Kayıt başladı",
        ["{0} · press {1} to stop"] = "{0} · durdurmak için {1}",
        ["Saving recording…"] = "Kayıt kaydediliyor…",
        ["Recording saved"] = "Kayıt kaydedildi",
        ["Recording not saved"] = "Kayıt kaydedilmedi",
        ["Fullscreen (F)"] = "Tam ekran (F)",
        ["Exit fullscreen (Esc)"] = "Tam ekrandan çık (Esc)",
        ["Settings saved"] = "Ayarlar kaydedildi",
        ["They apply when the recording stops"] = "Kayıt bitince uygulanacak",
        ["Settings applied."] = "Ayarlar uygulandı.",
        ["Replay Recorder: clips folder is getting big"] = "Replay Recorder: klip klasörü büyüyor",
        ["Your clips use {0:0.0} GB (warning limit {1:0} GB). Open the library to delete old clips."] = "Klipler {0:0.0} GB kullanıyor (uyarı sınırı {1:0} GB). Eski klipleri silmek için kitaplığı aç.",
        [Ffmpeg.InstallHint] = "ffmpeg bulunamadı. Başlat'a sağ tıkla, Terminal'i aç ve şunu çalıştır:  winget install Gyan.FFmpeg  sonra Replay Recorder'ı yeniden başlat.",
        ["No monitor found to capture."] = "Kaydedilecek monitör bulunamadı.",
        ["Your ffmpeg is too old for Windows Graphics Capture, so Desktop Duplication is used instead. It records at your screen's full resolution, whatever you picked in Settings. To update: open Terminal and run  winget upgrade Gyan.FFmpeg  then restart Replay Recorder."] =
            "ffmpeg sürümün Windows Graphics Capture için çok eski, bu yüzden Desktop Duplication kullanılıyor. Bu yöntem, Ayarlar'da ne seçtiysen seç ekranının tam çözünürlüğünde kaydeder. Güncellemek için Terminal'i aç,  winget upgrade Gyan.FFmpeg  çalıştır ve Replay Recorder'ı yeniden başlat.",
        ["HDR is on for this monitor. Clips are recorded in SDR, so very bright highlights may look clipped. Turn HDR off in Windows display settings for the most accurate colours."] =
            "Bu monitörde HDR açık. Klipler SDR kaydedilir, bu yüzden çok parlak yerler patlamış görünebilir. En doğru renkler için Windows ekran ayarlarından HDR'yi kapat.",
        ["Could not start ffmpeg: {0}"] = "ffmpeg başlatılamadı: {0}",
        ["Capture keeps failing. Check the log (tray menu > Open log)."] = "Yakalama sürekli başarısız oluyor. Günlüğe bak (tepsi menüsü > Günlüğü aç).",
        ["{0} is on {1}: using Intel Quick Sync instead of NVENC."] = "{0}, {1} üzerinde: NVENC yerine Intel Quick Sync kullanılıyor.",
    };
}
