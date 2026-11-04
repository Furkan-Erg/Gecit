# Geçit gözden geçirme kuralları

- Uygulama normal kullanıcıyla çalışır; yönetici yalnız `--elevated` yardımcı süreçte. Yeni bir yükseltme yolu,
  hizmete kullanıcının yazabildiği bir yoldan dosya / argüman vermek ya da hizmet izinlerini genişletmek [yüksek].
- Sayfalardan (tarayıcı sekmeleri, mağaza verisi) gelen metin araç çubuğuna / arayüze yalnız `textContent` ile yazılır;
  `innerHTML`'e dış veri ya da çeviri tablosundan gelmeyen metin yazmak [yüksek].
- DNS sırası bozulmaz: sistem DNS'i, DoH hizmeti yanıt vermeden 127.0.0.1'e çevrilmez; hizmet kaldırılmadan önce
  başka adrese çevrilir.
- Kullanıcıya görünen her metin çeviri katmanından geçer (Türkçe kaynak + İngilizce karşılık). Türkçe sabit metni
  doğrudan arayüze / bildirime yazan yeni kod [orta]. Günlük (log) ve geliştirici metinleri çevrilmez.
- Ayarlar dosyası (`settings.json`) geriye uyumlu kalır: yeni alanın varsayılanı olur, eski dosya hatasız açılır.
- Kod yorumları İngilizce (mevcut düzen).
