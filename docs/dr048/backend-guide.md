# Backend 実装手順

全体フローと環境変数対応表は [README.md](README.md)、Pocket ID / Infisical の事前準備は [setup.md](setup.md) を参照。

## 1. 設定値の追加

| 設定 | 値 | 用途 |
|---|---|---|
| Bridge トークンの issuer | `https://id.tl.tsunagu-sep.org` | 既存の Pocket ID 検証設定を流用可 |
| Bridge トークンの audience | 事前準備で登録した Resource 識別子 | 既存ログイン用トークンとの区別に必須 |
| ログインコード TTL | 60 秒程度 | 短寿命・ワンタイム |

## 2. 端末レジストリ

`device_id` → 拠点情報のマッピングを持つ（DB テーブルか、最初は設定ファイルでも可）。

| カラム | 例 | 備考 |
|---|---|---|
| device_id | `mid-terminal-01` | Infisical の拠点フォルダと同名にする運用 |
| location_id | `1` | ログイン後に遷移するスキャナー URL 用 |
| enabled | true | 端末単位で無効化できるように |

未登録・無効の `device_id` は 403 で拒否する。

## 3. M2M トークン検証

`POST /api/v1/auth/bridge/login` の前段で Bearer トークンを検証する。

- 署名: Pocket ID の JWKS（`/.well-known/openid-configuration` の `jwks_uri`）で RS256 検証。既存実装があれば流用
- `iss` が Pocket ID の issuer と一致
- `aud` が Bridge 用 Resource 識別子と一致（**既存のログイン用トークンを受け付けないための要**）
- `exp` 有効
- scope / permission に `admin:bridge-login` が含まれる

## 4. ログイン交換 API（契約: Bridge 実装済み、変更不可）

```http
POST /api/v1/auth/bridge/login
Authorization: Bearer <Pocket ID M2M access token>
Content-Type: application/json

{"device_id":"mid-terminal-01"}
```

成功レスポンス:

```json
{"launch_url":"https://admin.tl.tsunagu-sep.org/auth/bridge?code=<one-time-code>"}
```

実装要件:

- コードは暗号学的乱数（128bit 以上）で生成し、ハッシュ化して保存（TTL 60 秒、使用済みフラグ付き）
- コードに `device_id` / `location_id` を紐付けて保存する（Admin 遷移先の解決に使う）
- `launch_url` は **HTTPS・Admin origin・パス `/auth/bridge` 完全一致**で組み立てる。Bridge がこの 3 条件で検証しており、違反すると開かずに破棄される
- **Admin の長寿命 JWT を直接返さない**（launch_url + ワンタイムコードのみ）
- 失敗時は 401/403 を返す。Bridge は 30 秒間隔でリトライするため、恒久エラーでもレスポンスは軽量に
- ログにトークン・コードの生値を出さない

## 5. コード→セッション交換 API（パスは自由。以下は例）

```http
POST /api/v1/auth/bridge/exchange
Content-Type: application/json

{"code":"<one-time-code>"}
```

成功レスポンス例:

```json
{"token":"<Adminセッション用JWT>","location_id":1}
```

実装要件:

- コードの存在・TTL・未使用を検証し、**検証と使用済み化はアトミックに**行う（二重使用防止）
- 成功時に Admin セッションを発行。既存のログインセッションと同等の形式でよいが、subject は端末（Bridge 経由）と分かる形にしておくと監査しやすい
- `location_id` を返し、Admin が遷移先を組み立てられるようにする

## 6. セキュリティ要件まとめ

- [ ] audience 検証で既存ログイントークンの流用を遮断
- [ ] コードは 60 秒 TTL・ワンタイム・ハッシュ保存
- [ ] `/api/v1/auth/bridge/*` にレート制限（例: device_id あたり 10 req/min）
- [ ] トークン・コード・セッション JWT をログへ出力しない
- [ ] 監査ログ: いつ・どの device_id がログイン/失敗したか
