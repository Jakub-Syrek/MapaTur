# Wydania Androida (podpisany APK)

MapaTur jest dystrybuowany jako **podpisany APK** instalowany ze strony / z GitHub Releases
(sideload — poza Google Play). Ten dokument opisuje jednorazową konfigurację oraz wydawanie wersji.

> ⚠️ **Klucz podpisujący = tożsamość aplikacji na zawsze.** Ten sam keystore musi podpisywać KAŻDĄ
> kolejną wersję — inaczej aktualizacja „po wierzchu" jest niemożliwa (Android odrzuci APK podpisany
> innym kluczem). Zrób **kopię zapasową** keystore'a i haseł w bezpiecznym miejscu. Keystore **nie jest**
> i nie może być w repozytorium (`.gitignore` go blokuje).

## 1. Utwórz keystore (jednorazowo)

```bash
keytool -genkeypair -v \
  -keystore mapatur.keystore \
  -alias mapatur \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -storepass "WPISZ_HASLO_STORE" \
  -keypass  "WPISZ_HASLO_KLUCZA" \
  -dname "CN=Jakub Syrek, O=MapaTur, C=PL"
```

(`keytool` jest częścią JDK — np. `C:\Program Files\Microsoft\jdk-*\bin\keytool.exe`.)

## 2. Build lokalny — podpisany APK

```powershell
dotnet publish src/MapaTur.App/MapaTur.App.csproj -c Release -f net10.0-android `
  -p:AndroidSigningKeyStore="C:\sciezka\mapatur.keystore" `
  -p:AndroidSigningKeyAlias=mapatur `
  -p:AndroidSigningStorePass="HASLO_STORE" `
  -p:AndroidSigningKeyPass="HASLO_KLUCZA"
```

APK ląduje w `src/MapaTur.App/bin/Release/net10.0-android/.../*-Signed.apk` (arm64, format ustawiony w
`.csproj`: `AndroidPackageFormat=apk`, `RuntimeIdentifiers=android-arm64`). Bez podanego keystore'a
podpisywanie się nie włącza (release nadal się zbuduje, ale niepodpisany).

## 3. Sekrety w GitHub (jednorazowo) — dla automatu

Repo → **Settings → Secrets and variables → Actions → New repository secret**:

| Sekret | Wartość |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | base64 pliku keystore: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("mapatur.keystore"))` |
| `ANDROID_KEYSTORE_PASSWORD` | hasło store |
| `ANDROID_KEY_ALIAS` | `mapatur` |
| `ANDROID_KEY_PASSWORD` | hasło klucza |

## 4. Wydanie wersji

```bash
git tag v1.0.0
git push origin v1.0.0
```

Workflow `.github/workflows/release.yml` zbuduje podpisany APK (`ApplicationDisplayVersion` z tagu,
`ApplicationVersion` = numer runu = rosnący kod wersji) i opublikuje go jako **GitHub Release**.
Strona-lądowanie linkuje najnowszy asset.

## Do zrobienia przed publicznym wydaniem

- [ ] **`ApplicationId`** — teraz placeholder `com.companyname.mapatur.app`; ustaw docelowy (np.
      `pl.syrek.mapatur`) PRZED pierwszym publicznym wydaniem. Zmiana po wydaniu = osobna aplikacja
      (utrata aktualizacji + danych u użytkowników).
- [ ] **Dane mapowe** — APK nie zawiera mbtiles/DEM/orto; potrzebny mechanizm pobierania danych
      per-region przy 1. uruchomieniu (osobny krok).
- [ ] **Play Protect** — sideloadowane APK pokazują ostrzeżenie „nieznane źródło"; opisać w instrukcji
      instalacji na stronie.
