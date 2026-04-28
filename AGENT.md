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
- NDEF Text record が存在しないカードは送信対象外です。
- 同一 payload は 500ms 以内の連続検出を重複として抑制します。

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

## ログ

ログは標準出力に出ます。主な prefix は以下です。

- `[READ]`: 読み取り WebSocket、読み取り処理
- `[NFC]`: リーダー検出、カード monitor
- `[APP]`: アプリのライフサイクル

## 注意点

- 現行実装は UID/IDm の読み取りではなく、NDEF Text record の読み取りを行います。
- 書き込み用 WebSocket (`ws://127.0.0.1:8090`) は起動しません。
- WebSocket は loopback の平文 `ws://` です。リモート公開は想定していません。
- Origin 制限は実装されていません。
- Windows 実機、PC/SC リーダー、NFC タグを使った読み取り確認が必要です。
- `dotnet build` / `dotnet publish` で nullable warning が出る箇所があります。
