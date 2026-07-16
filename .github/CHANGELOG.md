# v1.0.1 - トポロジー誘導材質合成 for YMM4

プレビュー再生の再開時に`CreateSwapChainForHwnd`がE_ACCESSDENIEDで失敗する問題を修正したリリースです。
エフェクトの機能、パラメータ、処理結果に変更はありません。

---

## 修正

### Direct3D 11・Direct3D 12共有同期のフラッシュ保証

`TopologicalMaterialGpuInterop`の`BeginCompute`と`WaitForIdle`で、`ID3D11DeviceContext4::Signal`の直後に`ID3D11DeviceContext::Flush`を呼び出すようにしました。

#### 原因

`ID3D11DeviceContext4::Signal`はフェンスへの通知をDirect3D 11のコマンドバッファへ記録しますが、GPUへの即時送出は仕様上保証されません。

- `WaitForIdle`は通知を記録した直後に`ID3D11Fence::SetEventOnCompletion`で呼び出しスレッドを同期的に待機させていました。コマンドバッファを送出できる唯一のスレッド自身が待機するため、解像度変更時と破棄時にレンダースレッドが停止する可能性がありました。
- 停止した即時コンテキストは、YMM4が破棄した旧スワップチェーンの遅延破棄を進行できません。フリップモデルのスワップチェーンは同一ウィンドウへ同時に1つしか関連付けられないため、再生を再開した`CreateSwapChainForHwnd`がE_ACCESSDENIEDで失敗していました。
- `BeginCompute`の通知も後続の外部フラッシュに依存しており、Direct3D 12コンピュートキューの待機が解除される時点が不定でした。

#### 修正内容

両箇所で通知の直後に明示的なフラッシュを行い、Direct3D 12側の待機解除とCPU側の待機完了が外部のフラッシュに依存しない構成にしました。フレーム処理の同期順序、共有リソースの再利用、CPUへの読み戻しを行わない構成は変更していません。
