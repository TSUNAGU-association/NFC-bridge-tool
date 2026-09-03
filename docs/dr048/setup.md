# 事前準備（Pocket ID / Infisical）

Backend 実装（[backend-guide.md](backend-guide.md)）より先に済ませておくと結合テストがスムーズ。

## 1. Pocket ID: Bridge 用 confidential クライアント作成

- 新規 OIDC クライアントを「Public Client」トグル **OFF** で作成（Client Credentials は confidential クライアント限定）
- リダイレクト URI は不要（M2M のため）
- 発行された Client ID / Client Secret を Infisical `prod` 環境 `/bridge` の
  `NFC_BRIDGE_POCKETID_CLIENT_ID` / `NFC_BRIDGE_POCKETID_CLIENT_SECRET` に設定
- 既存の Admin / Backend 用 public クライアントには一切手を入れない

## 2. Pocket ID: API Resource と permission

- Backend を表す API Resource を登録し、その識別子を Infisical の `NFC_BRIDGE_POCKETID_RESOURCE` に設定
- Resource に `admin:bridge-login` permission を定義し、Bridge クライアントへ付与

## 3. Infisical

- `NFC_BRIDGE_LOGIN_EXCHANGE_URL` を Backend の実 URL（`https://<backend>/api/v1/auth/bridge/login`）に差し替え

## 補足

稼働中の Pocket ID インスタンス（`https://id.tl.tsunagu-sep.org`）が `client_credentials` grant と `client_secret_basic` / `client_secret_post` 認証に対応していることは discovery エンドポイント（`/.well-known/openid-configuration`）で確認済み。
