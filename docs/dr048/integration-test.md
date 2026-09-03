# 結合テストチェックリスト

Backend / Admin 実装後、Windows 実機の Bridge と通しで確認する。前提の設定は [setup.md](setup.md) を参照。

- [ ] Pocket ID から M2M トークンが取得できる（`curl` で client_credentials を直接叩いて確認）
- [ ] 正当なトークン + 登録済み device_id で `launch_url` が返る
- [ ] 既存ログイン用（public クライアント）のトークンでは 401/403 になる（audience 検証）
- [ ] 未登録 device_id は 403
- [ ] launch_url を開くと Admin がセッションを取得し MID.スキャナーへ遷移する
- [ ] 同じ code の 2 回目の交換は失敗する（ワンタイム）
- [ ] TTL 経過後の code は失敗する
- [ ] Bridge のログイン時刻に専用 Chrome が起動しスキャナーが表示される（`[AUTH]` ログ確認）
- [ ] ログアウト時刻に専用 Chrome だけが終了し、プロファイルが削除される
- [ ] ログアウト後にサーバー側セッションも失効している
- [ ] Backend 停止中は Bridge が 30 秒間隔でリトライし、復旧後に自動ログインする
