# DR048 Bridge 自動ログイン・ログアウト 連携ドキュメント

Bridge の Admin 自動ログイン・ログアウト機能（PR #9）を実際に動かすために必要な、周辺システムの実装・設定手順。Bridge 側の実装は完了している。

| ドキュメント | 対象 |
|---|---|
| [setup.md](setup.md) | 事前準備（Pocket ID / Infisical の設定） |
| [backend-guide.md](backend-guide.md) | Backend の実装手順 |
| [admin-guide.md](admin-guide.md) | Admin の実装手順 |
| [integration-test.md](integration-test.md) | 結合テストチェックリスト |

本書の契約部分（エンドポイントパス・リクエスト/レスポンス形式・URL 検証条件）は Bridge のコードが検証している値なので変更しないこと。変更する場合は Bridge の環境変数（Infisical）側も合わせて更新する。

## 全体フロー

```mermaid
sequenceDiagram
    participant B as Bridge (端末)
    participant P as Pocket ID
    participant BE as Backend
    participant C as 専用Chrome
    participant A as Admin (SPA)

    Note over B: 毎日 08:00（NFC_BRIDGE_AUTO_LOGIN_TIME）
    B->>P: POST /api/oidc/token<br/>grant_type=client_credentials + resource + scope
    P-->>B: access_token（M2M、aud=Backend Resource）
    B->>BE: POST /api/v1/auth/bridge/login<br/>Authorization: Bearer + {"device_id":"..."}
    BE->>BE: トークン検証・device_id 検証<br/>短寿命ワンタイムコード発行
    BE-->>B: {"launch_url":"https://admin.../auth/bridge?code=..."}
    B->>C: 専用プロファイルで launch_url を開く
    C->>A: GET /auth/bridge?code=...
    A->>BE: コードをセッションに交換
    BE-->>A: Admin セッション（ローカルJWT）
    A->>A: /leader/scanner?location_id=... へ遷移

    Note over B: ログアウト時刻（NFC_BRIDGE_AUTO_LOGOUT_TIME）
    B->>C: /logout/callback を開く（best-effort）
    C->>A: GET /logout/callback
    A->>BE: サーバー側セッション破棄
    B->>B: 専用Chrome終了 + プロファイル削除<br/>（ローカルJWTの物理削除を保証）
```

## 環境変数対応表（Bridge 側・Infisical 管理）

| Bridge 環境変数 | 対応する Backend / Admin 側の値 |
|---|---|
| `NFC_BRIDGE_POCKETID_CLIENT_ID` / `_SECRET` | Pocket ID の Bridge 用 confidential クライアント |
| `NFC_BRIDGE_POCKETID_RESOURCE` | Pocket ID に登録した Backend の Resource 識別子 = Backend が検証する audience |
| `NFC_BRIDGE_POCKETID_SCOPE`（既定 `admin:bridge-login`） | Backend が要求する permission |
| `NFC_BRIDGE_LOGIN_EXCHANGE_URL` | Backend のログイン交換 API の実 URL |
| `NFC_BRIDGE_ADMIN_ORIGIN` / `NFC_BRIDGE_ADMIN_LAUNCH_PATH` | Admin の origin と `/auth/bridge` のパス |
| `NFC_BRIDGE_ADMIN_LOGOUT_URL` | Admin の `/logout/callback` の URL |
| `NFC_BRIDGE_DEVICE_ID` | Backend の端末レジストリに登録する device_id |
