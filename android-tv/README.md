# My Coins — Android TV wrapper

A minimal, sideloadable Android TV app that opens the plugin's kid page
(`/KidsLimit/kid?token=…`) fullscreen in a WebView. All of the actual UI is
served by the Jellyfin plugin, so you build and install this **once**; every
future improvement ships with the plugin, not the APK.

## Build

### Easiest: GitHub Actions (no local toolchain)

The **Build Android TV App** workflow (`.github/workflows/android-tv.yml`) builds
the APK for you:

1. GitHub → **Actions** → *Build Android TV App* → **Run workflow**.
2. Optionally paste the kid page URL to bake it into the APK (see step 2 below);
   leave it empty to type the URL on the TV on first launch instead.
3. When the run finishes, download the **`my-coins-tv-apk`** artifact and unzip
   it to get `app-debug.apk`.

It also builds automatically on any push that touches `android-tv/`.

### Local: Android Studio

1. Open this `android-tv/` folder in Android Studio (it will generate the
   Gradle wrapper on first sync).
2. In the plugin settings (*Per-user limits* → your kid → *Kid page*), generate
   a kid token and copy the kid page URL.
3. Put that URL into `gradle.properties` as `KID_PAGE_URL=…` (recommended), or
   leave it empty to type the URL on the TV on first launch.
4. Build → Build APK(s).

## Install on the TV

Enable Developer options on the TV (Settings → Device → About → click *Build*
7×), turn on USB/network debugging, then:

```
adb connect <tv-ip>
adb install app-debug.apk   # the artifact from CI, or app/build/outputs/apk/debug/app-debug.apk from a local build
```

(Any sideloading app — e.g. "Send files to TV" — works too.)

The app appears in the TV launcher as **My Coins** with a gold-coin banner.
D-pad navigates the tiles, OK selects, BACK closes a dialog on the page
(HOME leaves the app). The URL entered on first launch can be changed by
clearing the app's data.
