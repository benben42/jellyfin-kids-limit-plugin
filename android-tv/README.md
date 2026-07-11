# My Coins — Android TV wrapper

A minimal, sideloadable Android TV app that opens the plugin's kid page
(`/KidsLimit/kid?token=…`) fullscreen in a WebView. All of the actual UI is
served by the Jellyfin plugin, so you build and install this **once**; every
future improvement ships with the plugin, not the APK.

## Build

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
adb install app/build/outputs/apk/release/app-release.apk   # or the debug APK
```

(Any sideloading app — e.g. "Send files to TV" — works too.)

The app appears in the TV launcher as **My Coins** with a gold-coin banner.
D-pad navigates the tiles, OK selects, BACK closes a dialog on the page
(HOME leaves the app). The URL entered on first launch can be changed by
clearing the app's data.
