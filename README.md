# RadikoUWP

Windows 10 Mobile / Desktop 向け radiko ライブストリーミング再生 UWP アプリ

## プロジェクト概要

radiko.jp のライブ放送を Windows 10 Mobile (ARM) および Windows 10 Desktop (x86/x64) で再生できる UWP (Universal Windows Platform) アプリです。  
radiko の 2 段階認証フローを実装し、エリアの放送局一覧を取得して HLS 形式でストリーミング再生します。  
アプリがバックグラウンドに移っても再生を継続できます。

## 必要な開発環境

| ツール | バージョン |
|--------|-----------|
| Visual Studio | 2017 (15.x) 以上 |
| Windows 10 SDK | 10.0.15063.0 (Creators Update) |
| .NET | .NET Framework 4.6.1 (UWP) |

Visual Studio 2017 インストール時に **ユニバーサル Windows プラットフォーム開発** ワークロードと  
**Windows 10 SDK (10.0.15063.0)** を選択してください。

## ビルド手順

1. `RadikoUWP.sln` を Visual Studio 2017 で開く
2. ソリューションエクスプローラーでプロジェクトを右クリック → **NuGet パッケージの復元** (必要な場合)
3. プラットフォームを `x86` / `x64` / `ARM` から選択
4. **ビルド** → **ソリューションのビルド** (Ctrl+Shift+B)

## Windows 10 Mobile への展開手順

### 実機への展開

1. Windows 10 Mobile デバイスで **開発者モード** を有効化  
   (設定 → 更新とセキュリティ → 開発者向け → 開発者モード)
2. USB でデバイスを PC に接続
3. Visual Studio でプラットフォームを **ARM** に設定
4. デバッグターゲットを **デバイス** に設定
5. **F5** または **デバッグ** → **デバッグの開始**

### エミュレーターでの実行

1. Visual Studio でプラットフォームを **x86** に設定
2. デバッグターゲットを **Mobile Emulator** から選択
3. **F5** で起動

## アセット画像の準備手順

`RadikoUWP/Assets/` フォルダには、ビルドのためのプレースホルダー PNG 画像が同梱されています (ラジコブルー `#00B4ED` の単色)。  
本番リリース用には以下のサイズで差し替えてください:

| ファイル名 | サイズ | 用途 |
|-----------|-------|------|
| `StoreLogo.png` | 50×50 | ストアロゴ |
| `Square44x44Logo.png` | 44×44 | タイル・タスクバーアイコン |
| `Square150x150Logo.png` | 150×150 | 標準タイル |
| `Wide310x150Logo.png` | 310×150 | ワイドタイル |
| `LockScreenLogo.png` | 24×24 | ロック画面バッジ |
| `SplashScreen.png` | 620×300 | スプラッシュスクリーン |

## アプリの使い方

1. アプリを起動する
2. **「認証」** ボタンをタップ → radiko への 2 段階認証が実行され、エリアが自動判定される
3. 放送局一覧が表示されたら、聴きたい放送局をタップして選択する
4. **「▶ 再生」** ボタンをタップして再生開始
5. **「■ 停止」** ボタンで停止

バックグラウンド再生対応のため、ホーム画面や他のアプリに切り替えても再生が継続します。

## プロジェクト構造

```
RadikoUWP/
├── RadikoUWP.sln
└── RadikoUWP/
    ├── RadikoUWP.csproj          # TargetPlatformVersion=10.0.15063.0, ARM/x86/x64
    ├── Package.appxmanifest      # internetClient + backgroundMediaPlayback
    ├── App.xaml / App.xaml.cs
    ├── MainPage.xaml / MainPage.xaml.cs
    ├── Models/
    │   └── Station.cs            # 放送局モデル (Id, Name, AreaId)
    ├── Services/
    │   ├── RadikoAuthService.cs  # 2段階認証 (auth1 → partial key → auth2)
    │   ├── RadikoStationService.cs  # 放送局一覧・ライブストリーム URL 取得
    │   ├── RadikoPlayerService.cs   # 再生制御 (MediaStreamSource → AdaptiveMediaSource の順に試行)
    │   ├── RadikoHlsMediaSource.cs  # HLS (ID3 + AAC/ADTS packed audio) を自前パースして MediaStreamSource に供給
    │   └── RadikoHttp.cs            # キャッシュ無効の Windows.Web.Http クライアント
    ├── Assets/                   # アプリアイコン・スプラッシュ画像
    └── Properties/
        └── AssemblyInfo.cs
```

## 認証フロー

radiko の 2 段階認証を以下の手順で実装しています:

1. **auth1**: `https://radiko.jp/v2/api/auth1` へ GET リクエスト  
   → `X-Radiko-AuthToken`, `X-Radiko-KeyLength`, `X-Radiko-KeyOffset` をレスポンスヘッダーから取得

2. **Partial Key 生成**:  
   認証キー `bcd151073c03b352e1ef2fd66c32209da9ca0afa` の `[offset, offset+length]` を切り出して Base64 エンコード

3. **auth2**: `https://radiko.jp/v2/api/auth2` へ GET リクエスト  
   → レスポンスボディ `"JP13,東京都,tokyo"` 形式からエリア ID を抽出

## ライブ再生フロー

1. `https://radiko.jp/v3/station/stream/pc_html5/{station_id}.xml` を取得し、  
   `timefree="0"` (ライブ) / `areafree="0"` (エリア内) の `playlist_create_url` を取り出す  
   (取得できない場合は `https://si-f-radiko.smartstream.ne.jp/so/playlist.m3u8` を使用)
2. `{playlist_create_url}?station_id={id}&l=15&lsid={32桁16進ランダム}&type=b` を  
   `X-Radiko-AuthToken` ヘッダー付きで取得 (マスター → チャンクリスト)
3. セグメント (ID3 タグ付き AAC/ADTS) を取得して ADTS フレームに分解し、`MediaStreamSource` で再生  
   プレイリストは TARGETDURATION の半分ごとに再取得して新しいセグメントを追加
4. セグメントが ADTS でない場合などは OS 標準の `AdaptiveMediaSource` にフォールバック

HTTP は UWP 既定のキャッシュを無効化したクライアントで行います (ライブのプレイリストが更新されない問題の回避)。

## 制限事項・注意事項

- radiko の利用には radiko.jp の利用規約への同意が必要です
- エリア判定は IP アドレスに基づきます (海外からは利用不可)
- radiko プレミアム (エリアフリー) には未対応です
- 認証トークンには有効期限があります。長時間再生で途切れた場合は「再認証」してください
- ストリーミング URL や認証フローは radiko.jp のサービス変更により動作しなくなる場合があります
- Windows 10 SDK 10.0.15063.0 が Visual Studio にインストールされていない場合はビルドできません
