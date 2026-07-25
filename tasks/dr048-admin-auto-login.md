# DR048 TSUNAGU Admin 自動ログイン・ログアウト

## Bridge側対応

- [x] 毎日指定時刻にPocket ID Client CredentialsでM2Mアクセストークンを取得する
- [x] BackendのBridgeログイン交換APIから短寿命のAdmin起動URLを取得する
- [x] Admin origin以外の起動URLを拒否する
- [x] 専用ChromeプロファイルでAdminを起動する
- [x] 指定時刻にAdminローカルセッションを削除して専用Chromeだけを終了する
- [x] 環境変数の検証と機密情報を含まないログを追加する

## Backend / Admin側の前提

- [ ] Pocket IDにBackend API resourceと`admin:bridge-login` permissionを作成する
- [ ] Bridge専用confidential OIDC clientへM2M permissionを付与する
- [ ] Backendへ`POST /api/v1/auth/bridge/login`を追加する
- [ ] BackendでM2M tokenの署名・issuer・audience・permission・許可device IDを検証する
- [ ] Backendで短寿命・一回限りのAdminログインコードを発行する
- [ ] Adminへ`/auth/bridge`を追加し、コード交換後に既存形式のログイン情報を保存する
- [ ] AdminのBridgeログイン時に古いローカルセッションを置換する

## 実機確認

- [ ] Windows端末で08:00以降の起動時に専用Chromeが開く
- [ ] 通常利用中のChromeとは別プロセス・別プロファイルになる
- [ ] ログアウト時刻に専用Chromeだけ終了する
- [ ] Client secret不正、ネットワーク断、Backend 4xx/5xx時に30秒後再試行する
