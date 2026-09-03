# Admin 実装手順

全体フローと環境変数対応表は [README.md](README.md)、Backend 側の API 仕様は [backend-guide.md](backend-guide.md) を参照。

## 1. `/auth/bridge` 画面（新設）

Bridge が専用 Chrome で開く入口。パスは Bridge の検証条件（`NFC_BRIDGE_ADMIN_LAUNCH_PATH`、既定 `/auth/bridge`）と一致させること。

処理フロー:

1. クエリ `?code=` を取得。無ければエラー表示
2. Backend のコード交換 API を呼ぶ
3. 成功: セッション（ローカル JWT）を保存し、`/leader/scanner?location_id=<レスポンスの location_id>` へ replace 遷移（履歴に code 付き URL を残さない）
4. 失敗（期限切れ・使用済み）: エラーを表示して終了。**リトライしない**（コードはワンタイム。再ログインは Bridge の次回サイクルに任せる）

注意:

- 画面は無人端末で表示されるため、操作を要求しない（自動で交換→遷移まで完結させる）
- code はログ・エラートラッカーに送らない

## 2. `/logout/callback` 画面（新設）

Bridge がログアウト時刻に best-effort で開く。パスは Bridge 側 `NFC_BRIDGE_ADMIN_LOGOUT_URL`（既定 `https://admin.tl.tsunagu-sep.org/logout/callback`）と一致させること。

処理フロー:

1. Backend にセッション破棄をリクエスト（サーバー側セッション・リフレッシュ系があれば失効）
2. ローカルストレージ / Cookie の JWT を削除
3. 「ログアウトしました」を表示（無人端末なので操作不要で完結）

補足: この画面が開けなくても Bridge が専用 Chrome プロファイルごと削除するためローカル JWT は残らない。この画面の役目は**サーバー側**セッションの後片付け。
