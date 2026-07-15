# v1.0.0 - トポロジー誘導材質合成 for YMM4

YukkuriMovieMaker4向けのトポロジー誘導材質合成エフェクトプラグインの初回リリースです。
現在のフレームからOklabの色特徴と明度の勾配流を求め、上昇側と下降側の到達点が同じ画素を一つの領域として扱います。
領域内の経験分布を6種類の材質分布へ写し、領域境界に従う反応拡散とscreened Poisson再構成を経て、凹凸と照明を合成します。
履歴フレーム、参照画像、深度情報、学習済みモデルは使用しません。
8言語のリソース構成のUIを備えます。

---

## 新機能

### 1. ComputeSharp計算シェーダー

`TopologicalMaterialGpuShaders.cs`は、共有テクスチャまたは整数画素バッファーから入力を受け取り、前処理、明度平滑化、勾配流、領域解決、分布写像、反応拡散、Poisson再構成、材質照明、出力変換を実行します。YMM4で使う経路は共有テクスチャを直接入出力とし、整数画素バッファーの経路は同期処理とテストに使用します。

#### 前処理

`SharedTextureMaterialPreprocessShader`と`MaterialPreprocessShader`は、BGRAのプリマルチプライド色をストレート色へ戻し、sRGBから線形RGBを経てOklabへ変換します。`features`にはOklabのL・a・bと入力アルファを、`scalar`にはOklabのLを格納します。アルファが0の画素は両方を0にします。

`ScalarSmoothShader`は5×5近傍の明度を、空間重みと明度差の重みで平均します。明度差の重みは`exp(-(difference²) / 0.01)`です。空間重みは位相尺度の2乗から求め、反復回数は位相尺度を丸めた1〜8回です。透明画素は標本から除外します。

#### 勾配流と領域構造

`FlowInitializeShader`は8近傍から、現在値より特徴閾値以上明るい画素と暗い画素を選びます。該当画素がない場合は現在画素を到達点とします。同値の場合は画素インデックスで順序を固定します。

`FlowCompressShader`は上昇側と下降側の参照をポインタージャンピングで圧縮します。反復回数は`ceil(log2(画素数))`です。各画素は最終的に、上昇側と下降側の到達点の組を保持します。

`TopologyResolveShader`は周囲8画素を調べ、異なる到達点の組に接する割合、中心明度と近傍平均から求める隆起量と谷量、同じ領域へつながる上下左右の4ビット接続マスクを格納します。透明画素の接続値は-1です。

| 出力成分 | 内容 |
|---|---|
| `X` | 周囲8画素のうち異なる領域に接する割合 |
| `Y` | `max(center - neighborAverage, 0)`で求める隆起量 |
| `Z` | `max(neighborAverage - center, 0)`で求める谷量 |
| `W` | 左・右・上・下が同じ領域かを表す4ビット接続マスク |

#### 領域内の経験分布写像

`SlicedTransportShader`は、OklabのL・a・bと`隆起量 - 谷量`の4成分を8方向へ射影します。標本の前半は現在画素を中心とした半径`max(patternScale × 2, 4)`の範囲から、後半は画像全体から選びます。候補の到達点の組が現在画素と異なる場合、または候補が透明な場合は標本に含めません。

各射影について有効標本内の順位を求め、`quantile = rank / validCount`を`sign(2 × quantile - 1) × sqrt(abs(2 × quantile - 1))`へ変換します。材質別の中心値と射影幅から目標射影を作り、分布輸送の強さで入力射影から目標射影へ移します。8方向から4成分を再構成し、入力アルファを維持します。

標本の選択は、上昇側到達点、下降側到達点、シード、標本番号から算出する整数ハッシュで決まります。時間値は使用しません。

#### 反応拡散

`ReactionInitializeShader`は、模様サイズで分割したセル座標、領域の到達点、シードから初期核を決めます。初期核の確率は布地が0.38、氷晶が0.24、その他の材質が0.18です。領域境界の割合が0.45を超える画素も初期核にします。初期振幅は領域の到達点から決まります。

`ReactionDiffusionShader`はGray-Scott型の2成分反応拡散を計算します。近傍間隔は`max(int(patternScale / 12), 1)`で、上下左右の近傍が現在画素と同じ到達点の組を持つ場合だけ参照します。feedとkillは材質ごとに切り替えます。

| 材質 | feed | kill |
|---|---:|---:|
| 陶器 | 0.037 | 0.060 |
| 鉱物 | 0.029 | 0.057 |
| 酸化金属 | 0.026 | 0.055 |
| 羊皮紙 | 0.046 | 0.063 |
| 氷晶 | 0.022 | 0.051 |
| 布地 | 0.054 | 0.062 |

#### screened Poisson再構成

`PoissonRhsShader`は、分布写像後の明度、反応拡散模様、隆起、谷、領域境界から目標明度とガイダンスのラプラシアンを作ります。上下左右の接続マスクが無効な方向は中心値を使うため、ガイダンスは領域境界を越えません。

`PoissonJacobiShader`は`(rhs + reconstruction × connectedNeighborSum) / (1 + 4 × reconstruction)`を反復し、各反復で0〜1へ制限します。接続マスクが無効な方向は中心値を使います。再構成が0のときはJacobi反復を省略し、目標明度をそのまま使います。

#### 材質照明と出力

`MaterialFinalizeShader`は再構成したLと分布写像後のa・bを線形RGBへ戻します。再構成明度の左右差と上下差から表面法線を求め、光源角度と光源高度から拡散反射を計算します。鏡面反射の指数と強さは材質ごとに切り替えます。布地では画素座標から交差する織り目を加えます。

線形RGBをsRGBへ戻し、入力アルファを掛けてプリマルチプライドにします。RGBはアルファ以下へ制限し、入力アルファをそのまま出力します。`MaterialBufferToSharedTextureShader`は結果を共有BGRAテクスチャへ、`MaterialBufferToPackedShader`は同期テスト用の整数画素バッファーへ書き込みます。

#### 品質設定

`TopologicalMaterialSettings.GetQuality`は、領域内標本数、反応拡散の反復回数、Poisson再構成の反復回数を品質ごとに返します。

| 品質 | 領域内標本数 | 反応拡散 | Poisson再構成 |
|---|---:|---:|---:|
| 標準 | 16 | 24 | 32 |
| 高品質 | 32 | 40 | 48 |
| 最高品質 | 48 | 64 | 80 |

`TopologicalMaterialPipeline`は各シェーダーを同じ`ComputeContext`へ記録し、必要な箇所にバリアーを挿入します。作業バッファーは処理済みの最大画素数を容量として保持し、同じ画素数以下のフレームでは再利用します。

---

### 2. Direct3D 11・Direct3D 12共有処理

`TopologicalMaterialGpuInterop`は、YMM4が使用するDXGIアダプターのLUIDと一致するComputeSharpの`GraphicsDevice`を選びます。別のアダプターへ処理を送らないため、共有リソースは同じGPU上に作成されます。

入力用と出力用に`ReadWriteTexture2D<Bgra32, Float4>`を確保し、共有ハンドルからDirect3D 11の`ID3D11Texture2D`を開きます。各テクスチャのDXGIサーフェスからDirect2Dビットマップを作り、入力側を専用の`ID2D1DeviceContext6`の描画先に設定します。

フレーム処理の同期順序は以下のとおりです。

1. Direct2Dが入力映像を共有入力テクスチャへ`CompositeMode.SourceCopy`で描画します。
2. Direct3D 11側が共有フェンスを通知し、Direct3D 12側が同じ値を待機します。
3. ComputeSharpが共有入力テクスチャから材質合成を実行し、共有出力テクスチャへ書き込みます。
4. Direct3D 12側が共有フェンスを通知し、Direct3D 11側が同じ値を待機します。
5. Direct2Dが共有出力テクスチャを後段のカスタムエフェクトへ渡します。

同じ解像度では共有テクスチャ、Direct3D 11テクスチャ、Direct2Dビットマップを再利用します。解像度が変わる場合だけGPUの完了を待って再作成します。通常のフレーム処理ではCPUへの画素読み戻しとCPUからの画素再転送を行いません。

Direct3D 12デバイス、同一アダプター、Direct3D 11・12共有テクスチャ、共有フェンスのいずれかを利用できない場合、`TryCreate`は`null`を返します。

---

### 3. Direct2Dカスタムシェーダーエフェクト

`TopologicalMaterialField.hlsl`は2入力の最終合成シェーダーです。入力0に元映像、入力1にComputeSharpが生成した材質映像を受け取ります。強さが0以下、または元映像のアルファが0以下の場合は元映像をそのまま返します。

材質映像のRGBをアルファ以下へ制限し、`lerp(source, material, amount)`で合成します。出力はプリマルチプライドを維持します。

`TopologicalMaterialFieldCustomEffect`は`[CustomEffect(2)]`の2入力エフェクトです。`Amount`は`SetValue`を介して定数バッファーへ渡し、0〜1へ制限します。

| フィールド | 型 | 範囲 |
|---|---|---|
| `Amount` | `float` | 0〜1 |
| `Pad0`〜`Pad2` | `float` | 16バイト境界へそろえる詰め物 |

`ConstantBuffer`は合計16バイトです。`MapInputRectsToOutputRect`は入力0の矩形を出力矩形とし、`MapOutputRectToInputRects`は2入力を出力矩形へ合わせます。位置をずらしたサンプリングは行わないため、出力範囲は入力0と一致します。

シェーダーリソース: `pack://application:,,,/TopologicalMaterialField;component/Shaders/TopologicalMaterialField.cso`（ps_5_0、`ShaderResourceUri.Get`が生成）

---

### 4. エフェクト定義

`TopologicalMaterialFieldEffect`はYMM4の映像エフェクトとして宣言されます。

`[VideoEffect]`属性は以下のパラメーターで宣言されます。

- 表示名: `Texts.TopologicalMaterialField`（ローカライズキー、日本語では「トポロジー誘導材質合成」）
- カテゴリー: `VideoEffectCategories.Filtering`・`VideoEffectCategories.Decoration`
- 検索タグ: `TagMaterial`・`TagTopology`・`TagStylize`
- `IsAviUtlSupported = false`によりAviUtl向けEXO出力は非対応
- `ResourceType = typeof(Texts)`でローカライズリソースを指定

`Label`プロパティは`Texts.TopologicalMaterialField`を返します。

公開プロパティは以下のとおりです。

| プロパティ | 型 | デフォルト | 内部範囲 | アニメーション |
|---|---|---|---|---|
| `Amount` | `Animation` | 100 | 0〜100 | あり |
| `Material` | `TopologicalMaterialMode` | `Ceramic` | 6種類 | なし |
| `Quality` | `TopologicalMaterialQuality` | `High` | 3種類 | なし |
| `TopologyScale` | `Animation` | 3 | 1〜8 | あり |
| `FeatureThreshold` | `Animation` | 1 | 0〜25 | あり |
| `Distribution` | `Animation` | 85 | 0〜100 | あり |
| `ColorVariation` | `Animation` | 70 | 0〜200 | あり |
| `PatternScale` | `Animation` | 24 | 1〜256 | あり |
| `PatternStrength` | `Animation` | 65 | 0〜200 | あり |
| `Reconstruction` | `Animation` | 100 | 0〜200 | あり |
| `Relief` | `Animation` | 75 | 0〜200 | あり |
| `LightAngle` | `Animation` | -35 | -180〜180 | あり |
| `LightElevation` | `Animation` | 42 | 1〜89 | あり |
| `Seed` | `int` | 0 | 0〜int.MaxValue | なし |

`GetAnimatables`は`Amount`・`TopologyScale`・`FeatureThreshold`・`Distribution`・`ColorVariation`・`PatternScale`・`PatternStrength`・`Reconstruction`・`Relief`・`LightAngle`・`LightElevation`を返します。

材質は`Ceramic`・`Mineral`・`OxidizedMetal`・`Parchment`・`IceCrystal`・`Textile`の6種類です。品質は`Balanced`・`High`・`Ultra`の3種類です。`Seed`へ負の値を設定した場合は0へ制限します。

`CreateExoVideoFilters`は空のシーケンスを返します。`CreateVideoEffect`は映像処理用のインスタンスを生成します。

---

### 5. フレームごとの更新

`TopologicalMaterialFieldEffectProcessor`は、Direct3D共有処理、ComputeSharpパイプライン、2入力の最終合成、出力位置を戻す`AffineTransform2D`を接続します。

各フレームでYMM4の`EffectDescription`からフレーム位置、アイテム長、FPSを取得し、11個のアニメーション値を評価します。

| パラメータ | パイプラインへの変換 |
|---|---|
| `Amount` | `value / 100`を最終合成へ渡し、0〜1へ制限 |
| `Material` | 列挙値を整数へ変換 |
| `Quality` | 列挙値のまま品質設定へ渡す |
| `TopologyScale` | pxのまま1〜8へ制限 |
| `FeatureThreshold` | `value / 100`を0〜0.25へ制限 |
| `Distribution` | `value / 100`を0〜1へ制限 |
| `ColorVariation` | `value / 100`を0〜2へ制限 |
| `PatternScale` | pxのまま1〜256へ制限 |
| `PatternStrength` | `value / 100`を0〜2へ制限 |
| `Reconstruction` | `value / 100`を0〜2へ制限 |
| `Relief` | `value / 100`を0〜2へ制限 |
| `LightAngle` | 度からラジアンへ変換 |
| `LightElevation` | 度からラジアンへ変換し、1〜89度相当へ制限 |
| `Seed` | 0以上へ制限 |

強さが0以下の場合はComputeSharpパイプラインを実行せず、元映像を返します。入力矩形の座標と寸法が有限であり、幅、高さ、画素数を`int`で表せる場合だけGPU処理を実行します。条件を満たさない場合は強さを0にして元映像を返します。

入力解像度に合わせて共有リソースを確保し、入力矩形の左上を原点として共有入力テクスチャへ描画します。ComputeSharpの処理後、共有出力ビットマップを`AffineTransform2D`で元の左上座標へ戻し、最終合成の入力1へ接続します。

`CreateEffect`は共有処理、ComputeSharpパイプライン、カスタムエフェクトの順に生成します。共有処理またはパイプラインを生成できない場合、カスタムエフェクトが無効な場合は`null`を返し、エフェクト全体をパススルーします。生成途中で失敗したリソースはその場で破棄します。

エフェクトチェーンのクリア時は2入力と変換入力を`null`へ戻し、初回更新状態へ戻します。破棄時はGPUの完了を待ってからパイプラインと共有リソースを破棄します。

---

### 6. ローカライズ

`Texts`クラスは`[AutoGenLocalizer]`属性を持つ`partial`クラスとして宣言されます。ソースジェネレーターが`Texts.csv`を処理し、各ロケールのリソースファイルを自動生成します。

対応リソース: 日本語（`ja-jp`）・英語（`en-us`）・中国語簡体字（`zh-cn`）・中国語繁体字（`zh-tw`）・韓国語（`ko-kr`）・スペイン語（`es-es`）・アラビア語（`ar-sa`）・インドネシア語（`id-id`）

ローカライズキーの一覧は以下のとおりです。

| キー | ja-jp |
|---|---|
| `TopologicalMaterialField` | トポロジー誘導材質合成 |
| `BasicGroup` | 基本 |
| `TopologyGroup` | 位相構造 |
| `DistributionGroup` | 材質分布 |
| `PatternGroup` | 微細構造 |
| `ReconstructionGroup` | 階調再構成 |
| `LightingGroup` | 照明 |
| `Amount` | 強さ |
| `AmountDescription` | 元映像と生成された材質場の合成量です |
| `Material` | 材質 |
| `MaterialDescription` | 分布輸送と表面応答に使用する材質モデルです |
| `Quality` | 品質 |
| `QualityDescription` | 領域標本数と反復回数を選びます |
| `TopologyScale` | 位相尺度 |
| `TopologyScaleDescription` | 勾配流を構成する輝度地形の平滑化尺度です |
| `FeatureThreshold` | 特徴閾値 |
| `FeatureThresholdDescription` | 微小な勾配を停留点として扱う閾値です |
| `Distribution` | 分布輸送 |
| `DistributionDescription` | 領域内の色と構造の経験分布を材質分布へ写す強さです |
| `ColorVariation` | 色変動 |
| `ColorVariationDescription` | 材質分布の色幅と中心色への移行量です |
| `PatternScale` | 模様サイズ |
| `PatternScaleDescription` | 反応拡散の標本間隔と初期核の大きさです |
| `PatternStrength` | 模様強度 |
| `PatternStrengthDescription` | 反応拡散模様が色と凹凸へ与える量です |
| `Reconstruction` | 再構成 |
| `ReconstructionDescription` | screened Poisson 方程式で位相構造と材質階調を統合する強さです |
| `Relief` | 凹凸 |
| `ReliefDescription` | 再構成された高さ場から作る表面法線の強さです |
| `LightAngle` | 光源角度 |
| `LightAngleDescription` | 画像平面上の光源方向です |
| `LightElevation` | 光源高度 |
| `LightElevationDescription` | 材質表面に対する光源の高さです |
| `Seed` | シード |
| `SeedDescription` | 領域標本と反応拡散の初期状態を決める固定値です |
| `MaterialCeramic` | 陶器 |
| `MaterialCeramicDescription` | 滑らかな暖色の素地と硬い鏡面反射です |
| `MaterialMineral` | 鉱物 |
| `MaterialMineralDescription` | 彩度幅が広い結晶質の材質分布です |
| `MaterialOxidizedMetal` | 酸化金属 |
| `MaterialOxidizedMetalDescription` | 金属反射と酸化層の暖色分布を組み合わせます |
| `MaterialParchment` | 羊皮紙 |
| `MaterialParchmentDescription` | 暖色の繊維質分布と柔らかな反射です |
| `MaterialIceCrystal` | 氷晶 |
| `MaterialIceCrystalDescription` | 寒色の透明感と鋭い鏡面反射です |
| `MaterialTextile` | 布地 |
| `MaterialTextileDescription` | 交差する織り目と低い鏡面反射です |
| `QualityBalanced` | 標準 |
| `QualityBalancedDescription` | 標本数と反復回数を抑えた設定です |
| `QualityHigh` | 高品質 |
| `QualityHighDescription` | 品質と処理量の基準設定です |
| `QualityUltra` | 最高品質 |
| `QualityUltraDescription` | 最多の標本数と反復回数を使用します |
| `TagMaterial` | 材質 |
| `TagTopology` | 位相 |
| `TagStylize` | 画像調 |
| `UpdateAvailableMessage` | 新しいバージョン {0} が公開されています。 |
