using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Fans a title+message out to every configured <see cref="NotificationTarget"/>.
/// Supported providers: ntfy, Pushover, Gotify, Discord and Slack incoming webhooks,
/// Telegram bots, an Apprise API server, and a generic JSON webhook — so anything
/// Apprise can reach is one hop away, and simple services need no middleman.
/// </summary>
public sealed class NotificationService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public NotificationService(IHttpClientFactory httpClientFactory, ILogger<NotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Sends to all enabled targets without blocking the caller.</summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification body.</param>
    public void Send(PluginConfiguration config, string title, string message) =>
        _ = SendAsync(config, title, message);

    /// <summary>Sends to all enabled targets and reports how many succeeded.</summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification body.</param>
    /// <returns>(succeeded, attempted) counts.</returns>
    public async Task<(int Succeeded, int Attempted)> SendAsync(
        PluginConfiguration config,
        string title,
        string message)
    {
        var targets = (config.NotificationTargets ?? new List<NotificationTarget>())
            .FindAll(t => t.Enabled);
        var succeeded = 0;

        foreach (var target in targets)
        {
            try
            {
                using var request = BuildRequest(target, title, message);
                if (request is null)
                {
                    continue;
                }

                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                succeeded++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "KidsLimit: {Type} notification to '{Url}' failed.",
                    target.Type,
                    target.Url);
            }
        }

        return (succeeded, targets.Count);
    }

    private static HttpRequestMessage? BuildRequest(NotificationTarget target, string title, string message)
    {
        switch (target.Type)
        {
            case NotificationTarget.TypeNtfy:
            {
                if (string.IsNullOrWhiteSpace(target.Url))
                {
                    return null;
                }

                var req = new HttpRequestMessage(HttpMethod.Post, target.Url)
                {
                    Content = new StringContent(message, Encoding.UTF8, "text/plain"),
                };
                req.Headers.TryAddWithoutValidation("Title", title);
                req.Headers.TryAddWithoutValidation("Tags", "coin");
                if (!string.IsNullOrWhiteSpace(target.Token))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
                }

                return req;
            }

            case NotificationTarget.TypePushover:
            {
                if (string.IsNullOrWhiteSpace(target.Token) || string.IsNullOrWhiteSpace(target.UserKey))
                {
                    return null;
                }

                return new HttpRequestMessage(HttpMethod.Post, "https://api.pushover.net/1/messages.json")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["token"] = target.Token,
                        ["user"] = target.UserKey,
                        ["title"] = title,
                        ["message"] = message,
                    }),
                };
            }

            case NotificationTarget.TypeGotify:
            {
                if (string.IsNullOrWhiteSpace(target.Url) || string.IsNullOrWhiteSpace(target.Token))
                {
                    return null;
                }

                var url = target.Url.TrimEnd('/') + "/message";
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent(new { title, message, priority = 5 }),
                };
                req.Headers.TryAddWithoutValidation("X-Gotify-Key", target.Token);
                return req;
            }

            case NotificationTarget.TypeDiscord:
                return string.IsNullOrWhiteSpace(target.Url) ? null :
                    new HttpRequestMessage(HttpMethod.Post, target.Url)
                    {
                        Content = JsonContent(new { content = "**" + title + "**\n" + message }),
                    };

            case NotificationTarget.TypeSlack:
                return string.IsNullOrWhiteSpace(target.Url) ? null :
                    new HttpRequestMessage(HttpMethod.Post, target.Url)
                    {
                        Content = JsonContent(new { text = "*" + title + "*\n" + message }),
                    };

            case NotificationTarget.TypeTelegram:
            {
                if (string.IsNullOrWhiteSpace(target.Token) || string.IsNullOrWhiteSpace(target.UserKey))
                {
                    return null;
                }

                var url = "https://api.telegram.org/bot" + target.Token + "/sendMessage";
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent(new { chat_id = target.UserKey, text = title + "\n" + message }),
                };
            }

            case NotificationTarget.TypeApprise:
                // Stateful endpoint (http://host:8000/notify/{key}). For the stateless
                // /notify endpoint, put the apprise URLs into the server-side config key.
                return string.IsNullOrWhiteSpace(target.Url) ? null :
                    new HttpRequestMessage(HttpMethod.Post, target.Url)
                    {
                        Content = JsonContent(new { title, body = message, type = "info" }),
                    };

            case NotificationTarget.TypeWebhook:
            default:
                return string.IsNullOrWhiteSpace(target.Url) ? null :
                    new HttpRequestMessage(HttpMethod.Post, target.Url)
                    {
                        Content = JsonContent(new { title, message }),
                    };
        }
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
}
