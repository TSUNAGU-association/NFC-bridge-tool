using System.Text;

// Infisical Agent などが端末上にレンダリングした .env を起動時に読み込む。
// 実環境変数が常に優先で、.env は未設定のキーにのみ適用する。
internal static class EnvFile
{
    private const string PathEnvVar = "NFC_BRIDGE_ENV_FILE";
    private const string DefaultFileName = ".env";

    public static void Load(Action<string> log)
    {
        var overridePath = Environment.GetEnvironmentVariable(PathEnvVar);
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(AppContext.BaseDirectory, DefaultFileName)
            : Path.GetFullPath(overridePath);

        if (!File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                log($"[APP] {PathEnvVar} で指定された .env が見つかりません: {path}");
            }
            return;
        }

        try
        {
            var applied = 0;
            var skipped = 0;
            foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }
                if (line.StartsWith("export ", StringComparison.Ordinal))
                {
                    line = line["export ".Length..].TrimStart();
                }

                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                if (Environment.GetEnvironmentVariable(key) is not null)
                {
                    skipped++;
                    continue;
                }

                Environment.SetEnvironmentVariable(key, StripQuotes(line[(eq + 1)..].Trim()));
                applied++;
            }

            // シークレットを含みうるためキー名・値はログに出さない
            log($"[APP] .env を読み込みました: {path} (適用 {applied} 件, 既存環境変数を優先 {skipped} 件)");
        }
        catch (Exception ex)
        {
            log($"[APP] .env の読み込みに失敗しました: {ex.Message}; 環境変数のみで続行します");
        }
    }

    private static string StripQuotes(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
