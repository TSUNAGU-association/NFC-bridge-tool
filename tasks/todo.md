# NFC Bridge App — 起動時バージョン表示 + 自動更新

## ゴール
- 起動時に現在バージョンをログ出力する
- 起動時に GitHub Releases の最新版をチェックし、新しければダウンロード→自動置き換え→再起動する

## 設計

### バージョン埋め込み
- `NfcBridgeApp.csproj` に `<Version>1.2.0</Version>` を追加（最新タグに合わせる）。
- `release.yml` で publish 時に `-p:Version=<tag without v>` を渡し、リリースビルドではタグ名がバージョンになるようにする。
  - タグが `vX.Y.Z` 以外（pre-release suffix 等）の場合に備えて、`X.Y.Z` 部分を抽出して `Version` に、フル文字列を `InformationalVersion` に入れる。
- 起動ログ: `[APP] NfcBridgeApp v1.2.0`

### 更新チェック / 適用フロー（Windows のみ）
1. アプリ起動最初に `https://api.github.com/repos/TSUNAGU-association/NFC-bridge-tool/releases/latest` を `HttpClient`(timeout 5s) で取得。
2. レスポンスから `tag_name` と `assets[].browser_download_url`（`NfcBridgeApp-win-x64-*.zip` で始まるもの）を読む。
3. semver 比較で現バージョンより新しければ更新処理に進む。
4. 一時ディレクトリ（`%TEMP%/NfcBridgeApp-update-<guid>/`）に zip をダウンロードし展開。
5. 自プロセス PID を埋め込んだ `apply-update.bat` を生成:
   - `tasklist` でこのプロセスが終わるまで待つ
   - `robocopy /E` で展開先から `AppContext.BaseDirectory` に上書きコピー
   - 新しい `NfcBridgeApp.exe` を `start` で起動
   - 一時ディレクトリを削除
6. `cmd /c start` で bat を hidden 起動して `Environment.Exit(0)`。
7. 失敗（ネットワーク・JSON・展開・コピー）時は警告ログを出して通常起動を続行。

### 安全装置
- 環境変数 `NFC_BRIDGE_SKIP_UPDATE=1` でスキップ可能。
- 非 Windows（Mac での dev 実行）はチェック自体スキップ。
- 既存ユーザー追加ファイルを消さないため `robocopy /MIR` ではなく `/E` を使う。
- 例外は全てキャッチして起動継続。

## タスク
- [x] `NfcBridgeApp.csproj` に `<Version>2.0.0</Version>` を追加
- [x] `Program.cs` に
  - [x] 起動時バージョン表示 `[APP] NfcBridgeApp v<ver>`
  - [x] `CheckAndApplyUpdateAsync` 実装
  - [x] GitHub API 取得 / semver 比較 / zip ダウンロード / 展開 / bat 生成 / 自プロセス終了
- [x] `.github/workflows/release.yml` で `Version` / `InformationalVersion` をタグから注入
- [x] `AGENT.md` を更新（バージョン表示と更新動作を記述）
- [x] Mac 上で `dotnet build` と `dotnet publish -r win-x64` が通ることを確認（0 警告 / 0 エラー）

## レビュー

### 変更ファイル
- `NfcBridgeApp/NfcBridgeApp.csproj`: `<Version>1.2.0</Version>` 追加
- `NfcBridgeApp/Program.cs`:
  - 起動時に `[APP] NfcBridgeApp v<ver>` を出力
  - `CheckAndApplyUpdateAsync` で `/releases/latest` を取得（5s timeout）
  - `NfcBridgeApp-win-x64-*.zip` アセットを semver 比較（`v` prefix・pre-release suffix を除去）
  - 新バージョンなら `%TEMP%` に zip を DL → 展開 → `apply-update.bat` を hidden 起動 → `Environment.Exit(0)`
  - bat は `tasklist` で自プロセスの終了を待ち `robocopy /E` で上書き、新 exe を `start` で再起動
  - 例外は全て catch してログのみ出して通常起動継続
  - `OperatingSystem.IsWindows()` ガードで Mac 実行時はスキップ
  - 環境変数 `NFC_BRIDGE_SKIP_UPDATE=1` でバイパス可
- `.github/workflows/release.yml`:
  - タグから `vX.Y.Z` の数値部を抽出して `-p:Version` に、フル文字列を `-p:InformationalVersion` に渡す
- `AGENT.md`: バージョン表示と自動更新の挙動・環境変数を追記、`[UPDATE]` ログ prefix を追加

### 既知の制約
- Windows 実機での `tasklist` / `robocopy` 動作は未検証（Mac 上では PCSC ともども動作試験不可）。
- `robocopy /E` を使うので、旧バージョンで存在し新版で削除されたファイルは残存する（実害は小さい）。`/MIR` にすると同 dir に置かれた利用者ファイルも消えてしまうため不採用。
- `/releases/latest` は GitHub の仕様上 prerelease/draft を除外するため、`v1.3.0-beta` 等は対象にならない。意図的。
- `NFC_BRIDGE_SKIP_UPDATE=1` 以外の停止手段は無し。社内端末で更新を一時停止したい場合はこの env var で制御する。

### 動作確認手順（Windows 側）
1. `NfcBridgeApp.exe` 起動
2. 標準出力に下記が出ることを確認
   ```
   HH:MM:SS [APP] NfcBridgeApp v1.2.0
   HH:MM:SS [UPDATE] checking for new release...
   HH:MM:SS [UPDATE] up to date (current=1.2.0, latest=v1.2.0)
   HH:MM:SS [READ] listening on ws://127.0.0.1:8080
   ```
3. 古いバイナリを `v1.0.0` 想定で実行 → 自動で `v1.2.0` に更新され、再起動後に新バージョンログが出ること
4. ネットワーク遮断状態で起動 → `[UPDATE] failed: ...` と出て通常起動が継続すること
5. `set NFC_BRIDGE_SKIP_UPDATE=1` で起動 → `[UPDATE] skipped via NFC_BRIDGE_SKIP_UPDATE=1` が出てチェックされないこと
