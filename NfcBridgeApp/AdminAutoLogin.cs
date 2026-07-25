using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed class AdminAutoLoginService : IDisposable
{
    private const string DefaultTokenEndpoint = "https://id.tl.tsunagu-sep.org/api/oidc/token";
    private const string DefaultAdminOrigin = "https://admin.tl.tsunagu-sep.org";
    private const string DefaultScope = "admin:bridge-login";
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);

    private readonly AdminAutoLoginOptions _options;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private Process? _browserProcess;
    private DateOnly? _lastLoginDate;
    private DateOnly? _lastLogoutDate;

    private AdminAutoLoginService(AdminAutoLoginOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NfcBridgeApp-AdminAutoLogin");
    }

    public static bool TryCreate(Action<string> log, out AdminAutoLoginService? service)
    {
        service = null;
        if (!IsEnabled(Environment.GetEnvironmentVariable("NFC_BRIDGE_ADMIN_AUTO_LOGIN")))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            log("[AUTH] Windows以外の環境のためAdmin自動ログインは無効です");
            return false;
        }

        if (!AdminAutoLoginOptions.TryLoad(out var options, out var error))
        {
            log($"[AUTH] 設定エラーのためAdmin自動ログインを無効化しました: {error}");
            return false;
        }

        service = new AdminAutoLoginService(options!, log);
        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log($"[AUTH] Admin自動ログインを開始しました login={_options.LoginTime:HH\\:mm} logout={FormatTime(_options.LogoutTime)} device={_options.DeviceId}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var currentTime = TimeOnly.FromDateTime(now);

            if (_options.LogoutTime is { } logoutTime &&
                currentTime >= logoutTime &&
                _lastLogoutDate != today)
            {
                await LogoutAndCloseAdminBrowserAsync(cancellationToken);
                _lastLogoutDate = today;
                _log("[AUTH] 自動ログアウト時刻のためAdminブラウザを終了しました");
            }

            var beforeLogout = _options.LogoutTime is null || currentTime < _options.LogoutTime.Value;
            if (currentTime >= _options.LoginTime && beforeLogout && _lastLoginDate != today)
            {
                if (await TryLoginAsync(cancellationToken))
                {
                    _lastLoginDate = today;
                }
            }

            await Task.Delay(LoopInterval, cancellationToken);
        }
    }

    public void Dispose()
    {
        CloseAdminBrowser();
        _http.Dispose();
    }

    private async Task<bool> TryLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            _log("[AUTH] Pocket IDからBridge用アクセストークンを取得しています...");
            var accessToken = await FetchAccessTokenAsync(cancellationToken);
            var launchUrl = await FetchLaunchUrlAsync(accessToken, cancellationToken);

            if (!IsAllowedLaunchUrl(launchUrl))
            {
                _log("[AUTH] Backendが許可されていない起動URLを返したためログインを中止しました");
                return false;
            }

            var chromePath = FindChromeExecutable();
            if (chromePath is null)
            {
                _log("[AUTH] Google Chromeが見つかりません。NFC_BRIDGE_CHROME_PATHを設定してください");
                return false;
            }

            CloseAdminBrowser();
            Directory.CreateDirectory(_options.BrowserProfileDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add($"--app={launchUrl}");
            startInfo.ArgumentList.Add($"--user-data-dir={_options.BrowserProfileDirectory}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--disable-default-apps");

            _browserProcess = Process.Start(startInfo);
            if (_browserProcess is null)
            {
                _log("[AUTH] Adminブラウザを起動できませんでした");
                return false;
            }

            _log("[AUTH] Adminへ自動ログインする専用Chromeを起動しました");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[AUTH] Admin自動ログインに失敗しました: {ex.Message}; 30秒後に再試行します");
            return false;
        }
    }

    private async Task<string> FetchAccessTokenAsync(CancellationToken cancellationToken)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["resource"] = _options.Resource,
            ["scope"] = _options.Scope,
        });
        using var response = await _http.PostAsync(_options.TokenEndpoint, body, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pocket ID token endpoint returned HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement) ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException("Pocket ID response does not contain access_token");
        }

        return tokenElement.GetString()!;
    }

    private async Task<Uri> FetchLaunchUrlAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ExchangeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { device_id = _options.DeviceId }),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Bridge login exchange returned HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("launch_url", out var launchUrlElement) ||
            !Uri.TryCreate(launchUrlElement.GetString(), UriKind.Absolute, out var launchUrl))
        {
            throw new InvalidOperationException("Bridge login exchange response does not contain a valid launch_url");
        }

        return launchUrl;
    }

    private bool IsAllowedLaunchUrl(Uri launchUrl) =>
        launchUrl.Scheme == Uri.UriSchemeHttps &&
        string.Equals(launchUrl.Host, _options.AdminOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
        launchUrl.Port == _options.AdminOrigin.Port;

    private string? FindChromeExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.ChromePath) && File.Exists(_options.ChromePath))
        {
            return _options.ChromePath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task LogoutAndCloseAdminBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browserProcess is null || _browserProcess.HasExited)
        {
            CloseAdminBrowser();
            return;
        }

        var chromePath = FindChromeExecutable();
        if (chromePath is not null)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = chromePath,
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add($"--app={_options.LogoutUrl}");
                startInfo.ArgumentList.Add($"--user-data-dir={_options.BrowserProfileDirectory}");
                startInfo.ArgumentList.Add("--no-first-run");
                using var logoutProcess = Process.Start(startInfo);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[AUTH] Adminログアウトページを開けませんでした: {ex.Message}");
            }
        }

        CloseAdminBrowser();
    }

    private void CloseAdminBrowser()
    {
        if (_browserProcess is null)
        {
            return;
        }

        try
        {
            if (_browserProcess.HasExited)
            {
                return;
            }

            if (_browserProcess.CloseMainWindow() && _browserProcess.WaitForExit(5000))
            {
                return;
            }

            _browserProcess.Kill(entireProcessTree: true);
            _browserProcess.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _log($"[AUTH] Adminブラウザの終了に失敗しました: {ex.Message}");
        }
        finally
        {
            _browserProcess.Dispose();
            _browserProcess = null;
        }
    }

    private static bool IsEnabled(string? value) =>
        value is not null &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string FormatTime(TimeOnly? value) => value?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "disabled";

    private sealed record AdminAutoLoginOptions(
        TimeOnly LoginTime,
        TimeOnly? LogoutTime,
        Uri TokenEndpoint,
        string ClientId,
        string ClientSecret,
        string Resource,
        string Scope,
        Uri ExchangeEndpoint,
        Uri AdminOrigin,
        Uri LogoutUrl,
        string DeviceId,
        string? ChromePath,
        string BrowserProfileDirectory)
    {
        public static bool TryLoad(out AdminAutoLoginOptions? options, out string error)
        {
            options = null;
            error = string.Empty;

            if (!TryReadTime("NFC_BRIDGE_AUTO_LOGIN_TIME", "08:00", out var loginTime))
            {
                error = "NFC_BRIDGE_AUTO_LOGIN_TIME must be HH:mm";
                return false;
            }

            TimeOnly? logoutTime = null;
            var rawLogoutTime = Environment.GetEnvironmentVariable("NFC_BRIDGE_AUTO_LOGOUT_TIME");
            if (!string.IsNullOrWhiteSpace(rawLogoutTime))
            {
                if (!TimeOnly.TryParseExact(rawLogoutTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLogoutTime))
                {
                    error = "NFC_BRIDGE_AUTO_LOGOUT_TIME must be HH:mm";
                    return false;
                }
                if (parsedLogoutTime <= loginTime)
                {
                    error = "NFC_BRIDGE_AUTO_LOGOUT_TIME must be later than NFC_BRIDGE_AUTO_LOGIN_TIME";
                    return false;
                }
                logoutTime = parsedLogoutTime;
            }

            var clientId = Required("NFC_BRIDGE_POCKETID_CLIENT_ID", ref error);
            var clientSecret = Required("NFC_BRIDGE_POCKETID_CLIENT_SECRET", ref error);
            var resource = Required("NFC_BRIDGE_POCKETID_RESOURCE", ref error);
            var exchangeUrl = Required("NFC_BRIDGE_LOGIN_EXCHANGE_URL", ref error);
            var deviceId = Required("NFC_BRIDGE_DEVICE_ID", ref error);
            if (!string.IsNullOrEmpty(error))
            {
                return false;
            }

            if (!TryReadHttpsUri("NFC_BRIDGE_POCKETID_TOKEN_URL", DefaultTokenEndpoint, out var tokenEndpoint))
            {
                error = "NFC_BRIDGE_POCKETID_TOKEN_URL must be an absolute HTTPS URL";
                return false;
            }
            if (!Uri.TryCreate(exchangeUrl, UriKind.Absolute, out var exchangeEndpoint) || exchangeEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                error = "NFC_BRIDGE_LOGIN_EXCHANGE_URL must be an absolute HTTPS URL";
                return false;
            }
            if (!TryReadHttpsUri("NFC_BRIDGE_ADMIN_ORIGIN", DefaultAdminOrigin, out var adminOrigin))
            {
                error = "NFC_BRIDGE_ADMIN_ORIGIN must be an absolute HTTPS URL";
                return false;
            }
            var defaultLogoutUrl = new Uri(adminOrigin!, "/logout/callback").AbsoluteUri;
            if (!TryReadHttpsUri("NFC_BRIDGE_ADMIN_LOGOUT_URL", defaultLogoutUrl, out var logoutUrl) ||
                !HasSameOrigin(logoutUrl!, adminOrigin!))
            {
                error = "NFC_BRIDGE_ADMIN_LOGOUT_URL must use the configured Admin origin";
                return false;
            }

            var scope = Environment.GetEnvironmentVariable("NFC_BRIDGE_POCKETID_SCOPE");
            var chromePath = Environment.GetEnvironmentVariable("NFC_BRIDGE_CHROME_PATH");
            var profileDirectory = Environment.GetEnvironmentVariable("NFC_BRIDGE_BROWSER_PROFILE_DIR");
            if (string.IsNullOrWhiteSpace(profileDirectory))
            {
                profileDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NfcBridgeApp",
                    "admin-browser-profile");
            }

            options = new AdminAutoLoginOptions(
                loginTime,
                logoutTime,
                tokenEndpoint!,
                clientId!,
                clientSecret!,
                resource!,
                string.IsNullOrWhiteSpace(scope) ? DefaultScope : scope.Trim(),
                exchangeEndpoint,
                adminOrigin!,
                logoutUrl!,
                deviceId!,
                string.IsNullOrWhiteSpace(chromePath) ? null : chromePath.Trim(),
                Path.GetFullPath(profileDirectory));
            return true;
        }

        private static bool TryReadTime(string name, string defaultValue, out TimeOnly value)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return TimeOnly.TryParseExact(
                string.IsNullOrWhiteSpace(raw) ? defaultValue : raw,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
        }

        private static bool TryReadHttpsUri(string name, string defaultValue, out Uri? value)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return Uri.TryCreate(string.IsNullOrWhiteSpace(raw) ? defaultValue : raw, UriKind.Absolute, out value) &&
                   value.Scheme == Uri.UriSchemeHttps;
        }

        private static bool HasSameOrigin(Uri left, Uri right) =>
            string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
            left.Port == right.Port;

        private static string? Required(string name, ref string error)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            if (string.IsNullOrEmpty(error))
            {
                error = $"{name} is required";
            }
            return null;
        }
    }
}
