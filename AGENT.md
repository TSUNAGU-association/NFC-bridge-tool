# AGENT.md

## アプリ概要

このリポジトリは、PC/SC 対応 NFC リーダーを使って NFC タグの NDEF Text record を読み取り、ローカル WebSocket 経由で外部アプリへ送信する .NET 8 コンソールアプリです。

- アプリ本体: `NfcBridgeApp/Program.cs`
- プロジェクト: `NfcBridgeApp/NfcBridgeApp.csproj`
- ターゲット: Windows x64
- フレームワーク: .NET 8
- 主な依存:
  - `Fleck` 1.2.0
  - `PCSC` 7.0.1

## ビルド

成果物を作るときは、以下の multi-file publish コマンドを使います。

```sh
dotnet publish NfcBridgeApp/NfcBridgeApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist-multi
```

このコマンドは Windows x64 向けの自己完結型成果物を `dist-multi/` に出力します。`PublishSingleFile=false` のため、単一 exe ではなく複数ファイル構成の配布物になります。

## Release

タグを push すると GitHub Actions が Windows x64 向けに publish し、zip を GitHub Release に添付します。
添付される `NfcBridgeApp-win-x64-<tag>.zip` は上記の `dist-multi/` と同じ multi-file publish 成果物で、`NfcBridgeApp.exe` と `NfcBridgeApp.pdb` を含みます。
GitHub が自動生成する `Source code (zip)` / `Source code (tar.gz)` はソース一式であり、ビルド成果物は含みません。

```sh
git tag v0.1.0
git push origin v0.1.0
```

## 実行環境

- 実行対象 OS は Windows を想定します。
- NFC リーダーは PC/SC 準拠のものを使用します。
- macOS 上では publish 自体は可能ですが、PC/SC 実機動作は Windows 環境で確認してください。
- 配布先 PC に .NET ランタイムを別途入れる必要はありません。`--self-contained true` で publish します。

## 読み取り仕様

アプリ起動時に読み取り用 WebSocket サーバーを開始します。

- URL: `ws://127.0.0.1:8080`
- NFC タグ挿入時に NDEF Text record を読み取ります。
- 読み取った文字列を、接続中の全クライアントへそのまま broadcast します。
- NDEF Type 4 を先に試し、失敗した場合は NDEF Type 2 を試します。
- NDEF Text record が存在しないカードはエラーコードを送信します。
- 同一 payload は 500ms 以内の連続検出を重複として抑制します。
- 読み取り失敗時は `ws://127.0.0.1:8080` にエラーコード文字列を送信します。
  - `ERR_NO_NDEF_TEXT_RECORD`: 読み取りはできたが NDEF Text record が存在しない
  - `ERR_NFC_READ_FAILED`: カードとの通信失敗、途中までしか読めない、または読み取り処理中の例外

読み取りクライアント例:

```js
const ws = new WebSocket("ws://127.0.0.1:8080");
ws.onmessage = (event) => console.log(event.data);
```

## リーダー監視

- PC/SC context を確立して利用可能なリーダー一覧を取得します。
- リーダーが見つからない場合は 3 秒ごとに再試行します。
- リーダー一覧が変化した場合は monitor を再起動します。
- `Ctrl+C` またはプロセス終了時にシャットダウン処理へ入ります。

## バージョン表示と自動更新

起動時に現在のバージョンを `[APP] NfcBridgeApp v<version>` の形式で出力します。
バージョンは csproj の `<Version>` で定義し、リリースビルドではタグ名から `release.yml` が `-p:Version` を上書きします。

起動直後に GitHub Releases (`TSUNAGU-association/NFC-bridge-tool`) の `latest` を確認し、新しいバージョンがあれば自動で適用します。
最新版だった場合は管理ダッシュボード (`https://admin.tl.tsunagu-sep.org/admin/dashboard`) を既定ブラウザで自動的に開きます。更新適用時は再起動後の起動で同じフローによりブラウザが開きます。

- 取得元: `https://api.github.com/repos/TSUNAGU-association/NFC-bridge-tool/releases/latest`
- 対象アセット: `NfcBridgeApp-win-x64-*.zip`
- 動作対象: Windows のみ（macOS では skip）
- フロー: zip を `%TEMP%/NfcBridgeApp-update-<guid>/` に DL → 展開 → `apply-update.bat` を hidden 起動 → 自プロセス終了 → bat が `tasklist` で終了を待ち、`robocopy /E` でインストール先を上書き → 新 exe を起動して staging を削除。
- 失敗時は警告ログのみ出して通常起動を続行します。
- 環境変数 `NFC_BRIDGE_SKIP_UPDATE=1` で更新チェックをスキップできます。

## Admin自動ログイン・ログアウト

`NFC_BRIDGE_ADMIN_AUTO_LOGIN=1` を設定すると、Bridgeは通常ブラウザでダッシュボードを開く代わりに、Pocket IDのClient CredentialsでBridge用アクセストークンを取得し、BackendのBridgeログイン交換APIから短寿命の`launch_url`を取得します。そのURLを専用Chromeプロファイルのアプリモードで開きます。

- ログイン時刻の既定値は毎日08:00です。
- ログアウト時刻を設定すると、同じ専用ChromeプロファイルでAdminのログアウトページを開いてローカルJWTを削除してから、Bridgeが起動した専用Chromeだけを終了します。利用者の通常Chromeは終了しません。
- Bridge終了時にも専用Chromeを終了します。
- ログイン時刻後にBridgeを起動した場合、その日のログアウト時刻前であれば直ちにログインします。
- 失敗時は30秒ごとに再試行します。
- Backendから返された`launch_url`はHTTPSかつ設定したAdmin originと一致する場合のみ開きます。
- Client secret、アクセストークン、起動URLはログへ出力しません。

必須環境変数:

```text
NFC_BRIDGE_ADMIN_AUTO_LOGIN=1
NFC_BRIDGE_POCKETID_CLIENT_ID=<Pocket ID confidential client ID>
NFC_BRIDGE_POCKETID_CLIENT_SECRET=<Pocket ID client secret>
NFC_BRIDGE_POCKETID_RESOURCE=<Backend API resource>
NFC_BRIDGE_LOGIN_EXCHANGE_URL=https://api.example.com/api/v1/auth/bridge/login
NFC_BRIDGE_DEVICE_ID=mid-terminal-01
```

任意環境変数:

```text
NFC_BRIDGE_AUTO_LOGIN_TIME=08:00
NFC_BRIDGE_AUTO_LOGOUT_TIME=20:00
NFC_BRIDGE_POCKETID_TOKEN_URL=https://id.tl.tsunagu-sep.org/api/oidc/token
NFC_BRIDGE_POCKETID_SCOPE=admin:bridge-login
NFC_BRIDGE_ADMIN_ORIGIN=https://admin.tl.tsunagu-sep.org
NFC_BRIDGE_ADMIN_LOGOUT_URL=https://admin.tl.tsunagu-sep.org/logout/callback
NFC_BRIDGE_CHROME_PATH=C:\Program Files\Google\Chrome\Application\chrome.exe
NFC_BRIDGE_BROWSER_PROFILE_DIR=C:\NfcBridge\admin-browser-profile
```

Bridgeログイン交換API契約:

```http
POST /api/v1/auth/bridge/login
Authorization: Bearer <Pocket ID M2M access token>
Content-Type: application/json

{"device_id":"mid-terminal-01"}
```

```json
{"launch_url":"https://admin.tl.tsunagu-sep.org/auth/bridge?code=<short-lived-one-time-code>"}
```

BackendはPocket IDアクセストークンの署名、issuer、audience、`admin:bridge-login` permissionを検証し、起動URLには短寿命かつ一度だけ利用可能なコードを使用してください。BridgeへAdminの長寿命JWTを直接返さないでください。

## ログ

ログは標準出力に出ます。主な prefix は以下です。

- `[READ]`: 読み取り WebSocket、読み取り処理
- `[NFC]`: リーダー検出、カード monitor
- `[APP]`: アプリのライフサイクル
- `[UPDATE]`: 起動時の自動更新チェック / 適用
- `[AUTH]`: Admin自動ログイン / ログアウト

## 注意点

- 現行実装は UID/IDm の読み取りではなく、NDEF Text record の読み取りを行います。
- 書き込み用 WebSocket (`ws://127.0.0.1:8090`) は起動しません。
- WebSocket は loopback の平文 `ws://` です。リモート公開は想定していません。
- Origin 制限は実装されていません。
- Windows 実機、PC/SC リーダー、NFC タグを使った読み取り確認が必要です。
- `dotnet build` / `dotnet publish` で nullable warning が出る箇所があります。
