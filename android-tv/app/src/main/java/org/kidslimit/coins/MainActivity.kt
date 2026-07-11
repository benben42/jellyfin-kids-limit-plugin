package org.kidslimit.coins

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.os.Bundle
import android.text.InputType
import android.view.KeyEvent
import android.view.WindowManager
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.EditText

/**
 * Dumb fullscreen WebView shell around the plugin-served kid page
 * (`/KidsLimit/kid?token=…`). All UI lives server-side in the Jellyfin plugin,
 * so this APK is sideloaded once and never needs updating.
 *
 * The URL comes from gradle.properties (KID_PAGE_URL, baked at build time) or,
 * when left empty, from a one-time prompt stored in SharedPreferences.
 */
class MainActivity : Activity() {

    private lateinit var webView: WebView

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        webView = WebView(this)
        webView.settings.javaScriptEnabled = true
        webView.settings.domStorageEnabled = true
        webView.settings.mediaPlaybackRequiresUserGesture = false
        webView.webViewClient = WebViewClient()
        webView.isFocusable = true
        setContentView(webView)

        val url = BuildConfig.KID_PAGE_URL.ifEmpty {
            getPreferences(MODE_PRIVATE).getString("url", "").orEmpty()
        }
        if (url.isEmpty()) {
            promptForUrl()
        } else {
            webView.loadUrl(url)
        }
    }

    private fun promptForUrl() {
        val input = EditText(this).apply {
            inputType = InputType.TYPE_TEXT_VARIATION_URI
            hint = "http://192.168.1.10:8096/KidsLimit/kid?token=…"
        }
        AlertDialog.Builder(this)
            .setTitle(R.string.url_prompt)
            .setView(input)
            .setCancelable(false)
            .setPositiveButton(R.string.save) { _, _ ->
                val url = input.text.toString().trim()
                getPreferences(MODE_PRIVATE).edit().putString("url", url).apply()
                if (url.isEmpty()) promptForUrl() else webView.loadUrl(url)
            }
            .show()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // Remote BACK closes the page's overlay (the page listens for Escape) instead of
        // exiting the app — this is a kid kiosk; HOME is how you leave it.
        if (event.keyCode == KeyEvent.KEYCODE_BACK) {
            if (event.action == KeyEvent.ACTION_UP) {
                webView.evaluateJavascript(
                    "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}));",
                    null,
                )
            }
            return true
        }
        return super.dispatchKeyEvent(event)
    }
}
