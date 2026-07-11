namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// One push-notification destination for rewards events (chore claims, test pings).
/// The <see cref="Type"/> decides how <see cref="Url"/>/<see cref="Token"/>/<see cref="UserKey"/>
/// are used — see <see cref="Services.NotificationService"/>. XML-serializer friendly.
/// </summary>
public class NotificationTarget
{
    /// <summary>Provider: ntfy, Discord/Slack webhooks, Apprise API, generic JSON webhook.</summary>
    public const string TypeNtfy = "Ntfy";

    /// <summary>Provider: Pushover.</summary>
    public const string TypePushover = "Pushover";

    /// <summary>Provider: Gotify.</summary>
    public const string TypeGotify = "Gotify";

    /// <summary>Provider: Discord incoming webhook.</summary>
    public const string TypeDiscord = "Discord";

    /// <summary>Provider: Slack incoming webhook.</summary>
    public const string TypeSlack = "Slack";

    /// <summary>Provider: Telegram bot API.</summary>
    public const string TypeTelegram = "Telegram";

    /// <summary>Provider: Apprise API server (stateful /notify/{key} endpoint).</summary>
    public const string TypeApprise = "Apprise";

    /// <summary>Provider: generic JSON webhook (<c>{"title": …, "message": …}</c>).</summary>
    public const string TypeWebhook = "Webhook";

    /// <summary>Gets or sets the provider type (one of the Type* constants).</summary>
    public string Type { get; set; } = TypeNtfy;

    /// <summary>
    /// Gets or sets the destination URL. Ntfy: topic URL. Gotify: server base URL.
    /// Discord/Slack: incoming webhook URL. Apprise: full notify endpoint
    /// (e.g. http://host:8000/notify/mykey). Webhook: any URL. Pushover/Telegram: unused.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the credential. Pushover: application token. Gotify: app token.
    /// Telegram: bot token. Ntfy: optional access token (Bearer). Others: unused.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the recipient key. Pushover: user key. Telegram: chat id.
    /// Others: unused.
    /// </summary>
    public string UserKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this target is active.</summary>
    public bool Enabled { get; set; } = true;
}
