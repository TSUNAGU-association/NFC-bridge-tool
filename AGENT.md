# AGENT.md

## アプリ概要

このリポジトリは、PC/SC 対応 NFC リーダーを使って NFC タグの NDEF Text record を読み書きし、ローカル WebSocket 経由で外部アプリと連携する .NET 8 コンソールアプリです。

- アプリ本体: `NfcBridgeApp/Program.cs`
- プロジェクト: `NfcBridgeApp/NfcBridgeApp.csproj`
- ターゲット: Windows x64
- フレームワーク: .NET 8
- 主な依存:
  - `Fleck` 1.2.0
  - `PCSC` 7.0.1
  - `PCSC.Iso7816` 7.0.1

## ビルド

成果物を作るときは、以下の multi-file publish コマンドを使います。

```sh
dotnet publish NfcBridgeApp/NfcBridgeApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist-multi
```

このコマンドは Windows x64 向けの自己完結型成果物を `dist-multi/` に出力します。`PublishSingleFile=false` のため、単一 exe ではなく複数ファイル構成の配布物になります。

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
- 書き込み処理中は読み取りイベントをスキップします。

読み取りクライアント例:

```js
const ws = new WebSocket("ws://127.0.0.1:8080");
ws.onmessage = (event) => console.log(event.data);
```

## 書き込み仕様

アプリ起動時に書き込み用 WebSocket サーバーを開始します。

- URL: `ws://127.0.0.1:8090`
- JSON メッセージで書き込みを要求します。
- `id` の値を NDEF Text record としてカードへ書き込みます。
- 同時に処理できる書き込みは 1 件のみです。
- 書き込み要求から 10 秒間、300ms 間隔でリーダー/カードを探します。
- NDEF Type 4 書き込みを先に試し、失敗した場合は NDEF Type 2 書き込みを試します。
- Type 2 では通常の 4 byte page write を試し、失敗時に PN532 direct NTAG WRITE へフォールバックします。
- Type 2 書き込み後は read-back verify を行います。

書き込み要求:

```json
{
  "type": "write",
  "id": "sample-id"
}
```

成功レスポンス:

```json
{
  "type": "write_result",
  "success": true,
  "id": "sample-id"
}
```

失敗レスポンス:

```json
{
  "type": "write_result",
  "success": false,
  "error": "エラー内容"
}
```

書き込みクライアント例:

```js
const ws = new WebSocket("ws://127.0.0.1:8090");
ws.onopen = () => ws.send(JSON.stringify({ type: "write", id: "sample-id" }));
ws.onmessage = (event) => console.log(JSON.parse(event.data));
```

## リーダー監視

- PC/SC context を確立して利用可能なリーダー一覧を取得します。
- リーダーが見つからない場合は 3 秒ごとに再試行します。
- リーダー一覧が変化した場合は monitor を再起動します。
- `Ctrl+C` またはプロセス終了時にシャットダウン処理へ入ります。

## ログ

ログは標準出力に出ます。主な prefix は以下です。

- `[READ]`: 読み取り WebSocket、読み取り処理
- `[WRITE]`: 書き込み WebSocket、書き込み処理
- `[NFC]`: リーダー検出、カード monitor
- `[APP]`: アプリのライフサイクル

## 注意点

- 現行実装は UID/IDm の読み取りではなく、NDEF Text record の読み書きを行います。
- WebSocket は loopback の平文 `ws://` です。リモート公開は想定していません。
- Origin 制限は実装されていません。
- Windows 実機、PC/SC リーダー、NFC タグを使った読み書き確認が必要です。
- `dotnet build` / `dotnet publish` で nullable warning が出る箇所があります。
