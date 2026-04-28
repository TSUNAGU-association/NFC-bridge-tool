# NFC Bridge App — 実装計画

## ゴール
Mac で開発し、Windows (x64) で動作するローカルNFC中継アプリ。
PCSC準拠リーダー（PaSori等）で NDEF Text record を読み取り、ローカルWebSocketで配信する。

## 仕様
- WebSocket: `ws://127.0.0.1:8080`
- カード検知 → NDEF Text record の文字列をそのまま broadcast
- 二重読み込み防止: 同一 payload + 500ms 以内は破棄
- リーダー切断/再接続: 自動復帰（監視ループ）
- 書き込み用 WebSocket (`ws://127.0.0.1:8090`) は起動しない
- 配布形態: `dotnet publish NfcBridgeApp/NfcBridgeApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist-multi`

## 技術スタック
- .NET 8.0 (LTS)
- Fleck — 軽量 WebSocket サーバ
- PCSC — Windows のスマートカード API ラッパー
  - 注: PCSC は Windows でのみ動作（winscard.dll 依存）。Mac 上ではビルドのみ可能、実行は Windows 必須。

## タスク
- [x] tasks/todo.md に計画を書く（このファイル）
- [x] dotnet-install.sh で SDK 8.0.420 を `~/.dotnet` に導入（cask は sudo 要のため不採用）
- [x] `dotnet new console -n NfcBridgeApp` でプロジェクト作成
- [x] `dotnet add package Fleck` (1.2.0) / `PCSC` (7.0.1)
- [x] csproj は `net8.0` のまま据え置き（PCSC v7 は netstandard 互換、Windows 実行時のみ winscard.dll を解決）
- [x] Program.cs を実装
  - [x] WebSocketServer 起動 + クライアント管理（ConcurrentDictionary）
  - [x] PCSC コンテキスト確立 + リーダ列挙
  - [x] MonitorFactory で CardInserted を購読
  - [x] NDEF Type 4 / Type 2 から Text record を読み取り
  - [x] 二重読み込み防止（直近 payload + 500ms ウィンドウ）
  - [x] リーダ切断時の再試行ループ（3秒ごとにトポロジ再確認）
  - [x] Ctrl+C / プロセス終了でクリーンシャットダウン
- [x] `dotnet publish NfcBridgeApp/NfcBridgeApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist-multi` を実行
- [x] 生成された Windows x64 配布物を確認
- [x] レビュー欄に結果と既知の制約を記載

## 既知の制約
- PCSC は Windows API 依存 → Mac でランタイム動作確認は不可。Windows での実機テストが必要。
- Mac 上ではビルドのみ検証する（`dotnet build`が通ること）。
- `RuntimeIdentifier=win-x64` でクロスコンパイルするため、Mac 上でも publish 自体は通る想定。

## レビュー

### 成果物
- ソース: `NfcBridgeApp/Program.cs`
- 配布バイナリ: `NfcBridgeApp/bin/Release/net8.0/win-x64/publish/NfcBridgeApp.exe`
  - Windows x64 console app
  - multi-file / .NET ランタイム同梱（要件: 配布先に .NET 不要）

### 検証状況
- [x] Mac 上で `dotnet build` 通過（0 警告 / 0 エラー）
- [x] Mac 上で `dotnet publish -r win-x64 --self-contained` 通過
- [ ] Windows + PaSori 等の実機での動作確認（PCSC は winscard.dll 依存で Mac では実行不可）

### 動作確認手順（Windows 側）
1. `NfcBridgeApp.exe` を Windows にコピーして起動
2. ブラウザの DevTools コンソールで:
   ```js
   const ws = new WebSocket('ws://127.0.0.1:8080');
   ws.onmessage = e => console.log('NDEF text:', e.data);
   ```
3. リーダにカードをかざし、NDEF Text record の文字列がログ出力されること
4. 同一カードを連続でかざしても 500ms 以内は重複送信されないこと
5. リーダを USB から抜く → 3 秒以内に「reader topology changed」、再接続で復帰すること

### 既知の制約 / 今後の改善余地
- **CORS / Origin フィルタ**: 現状 Fleck はすべての Origin を受け入れる。本番運用で特定ドメインに絞る場合は `OnOpen` で `socket.ConnectionInfo.Origin` を検査して `Close()` する処理を追加。
- **WSS（TLS）非対応**: ローカル loopback のみのため平文 ws のまま。リモート公開しないこと。
- **リーダ複数台**: 現状すべての検知リーダを並列に監視。同時刻に複数台で別カード読み取り時の挙動は未テスト。
- **設定の外出し**: ポート番号・dedupe ウィンドウは定数。必要なら `appsettings.json` 化。
- **macOS 実行**: PCSC v7 は pcsclite 経由で macOS でも動く。`-r osx-arm64` で publish すれば Mac でも動作試験可能（要 pcsclite + USBリーダ）。

### 起動コマンド（Windows）
```
NfcBridgeApp.exe
```
ログ:
```
HH:MM:SS [READ] listening on ws://127.0.0.1:8080
HH:MM:SS [NFC] readers: SONY FeliCa Port/PaSoRi 3.0 0
HH:MM:SS [READ] -> 1 client(s): sample text
```
