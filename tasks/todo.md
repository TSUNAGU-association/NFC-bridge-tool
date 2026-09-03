# Infisical 連携用 .env 読み込み対応

対象ブランチ: `codex/DR048-admin-auto-login`（PR #9 に直接 push）

## 背景・方針

拠点ごとに異なる環境変数（DEVICE_ID / SCANNER_URL / ログアウト時刻など）と共通シークレット
（Pocket ID クレデンシャル等）を Infisical で一元管理したい。

- 配布方式は **Infisical Agent が端末上に `.env` をレンダリング**し、Bridge が起動時に読む
- `infisical run` ラッパー方式は自動更新（apply-update.bat が新 exe を直接起動）で
  環境変数が失われるため不採用
- Bridge 側の対応は「.env 読み込み」のみ。Infisical 依存はコードに入れない
  （Agent が無い環境では従来どおり環境変数だけで動く）

## 仕様

- 起動直後（自動更新チェックより前）に `.env` を読み込む
  - 既定パス: exe と同じディレクトリの `.env`
  - `NFC_BRIDGE_ENV_FILE` でパスを上書き可能
- **実環境変数が優先**。`.env` は未設定のキーにのみ適用（端末個別の一時上書きを可能にする）
- 書式: `KEY=VALUE`。空行・`#` コメント・`export ` prefix 許容、前後の引用符は除去
- 値はログに出さない（適用件数のみログ）
- 自動更新の robocopy /E は staging に無いファイルを消さないため、exe 横の `.env` は更新後も残る

## タスク

- [x] 仕様を本ファイルに記載
- [x] Redmine にチケット起票（tsunagu-link #123、進行中）
- [x] `NfcBridgeApp/EnvFile.cs` 追加（.env ローダー）
- [x] `Program.cs`: Main 冒頭で `EnvFile.Load()` を呼ぶ
- [x] `Program.cs`: `ScannerUrl` を static readonly フィールドから遅延評価プロパティに変更
      （static 初期化が .env 読み込みより先に走り、NFC_BRIDGE_SCANNER_URL が反映されないため）
- [x] `.gitignore` に `.env` を追加
- [x] AGENT.md に `.env` 仕様と Infisical Agent 構成例を追記
- [x] Release ビルド（警告0・エラー0）確認
- [x] macOS スモークテスト（.env あり/なし/上書きの挙動確認）
- [x] Redmine に結果記録・作業時間記録
- [x] PR #9 ブランチへ commit & push（ユーザー指示により PR #9 に追加）
- [ ] Windows 実機での確認（Infisical Agent + 実際の .env レンダリング）

## レビュー

- macOS スモークテスト結果（fake `open` でブラウザ起動を抑止して確認）
  - .env あり: 適用 2 件、`NFC_BRIDGE_SKIP_UPDATE` / `NFC_BRIDGE_SCANNER_URL`（引用符除去込み）反映 OK
  - 実環境変数と競合: 実環境変数が優先（既存環境変数を優先 1 件）OK
  - .env なし: 従来どおりデフォルト URL で動作 OK
  - ログにはパスと件数のみ。キー名・値は出力されない
- Release ビルド警告 0・エラー 0、win-x64 self-contained publish 成功
- 変更ファイル: EnvFile.cs（新規）/ Program.cs / AGENT.md / .gitignore / tasks/todo.md
